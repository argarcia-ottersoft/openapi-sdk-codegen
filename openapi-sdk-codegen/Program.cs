using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using System.Text;
using System.Text.RegularExpressions;

await using FileStream stream = File.OpenRead(args[0]);

OpenApiDocument? context = new OpenApiStreamReader().Read(stream, out OpenApiDiagnostic? _);

var files = new Dictionary<string, StringBuilder>();

foreach (var path in context.Paths)
{
    string[] parts = path.Key.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2) continue;

    string fileName = parts[0];
    if (!files.TryGetValue(fileName, out StringBuilder? file))
    {
        file = new StringBuilder(JavaScriptFileHeader());
        files.Add(fileName, file);
    }

    string functionName = ToCamelCase(parts[1]);
    file.AppendLine(JavaScriptFunctions(functionName, path));
}

Directory.CreateDirectory(args[1]);

foreach ((string? fileName, StringBuilder? rawContent) in files)
{
    string filePath = Path.Join(args[1], $"{fileName}.server.js");

#if DEBUG
    Console.WriteLine(filePath);
#endif

    var content = rawContent.ToString();

#if DEBUG
    Console.WriteLine(content);
#endif

    if (Environment.NewLine == "\r\n")
    {
        content = content.Replace("\r", string.Empty);
    }

    await File.WriteAllTextAsync(filePath, content);
}

var rawModelsContent = new StringBuilder();
string modelsFilePath = Path.Join(args[1], "Models.d.ts");

#if DEBUG
Console.WriteLine(modelsFilePath);
#endif

foreach ((string key, OpenApiSchema? schema) in context.Components.Schemas)
{
    rawModelsContent.AppendLine(@$"export interface {key} {{
{SchemaProperties(schema)}
}}
");
}

var modelsContent = rawModelsContent.ToString();

#if DEBUG
Console.WriteLine(modelsContent);
#endif

if (Environment.NewLine == "\r\n")
{
    modelsContent = modelsContent.Replace("\r", string.Empty);
}
await File.WriteAllTextAsync(modelsFilePath, modelsContent);

string SchemaProperties(OpenApiSchema schema)
{
    var properties = schema.Properties.Select(x => $"  {x.Key}: {ConvertToTypeScript(x.Value)}{NullableSchema(x.Value)};");
    return string.Join(Environment.NewLine, properties);
}

string JavaScriptFunctions(string name, KeyValuePair<string, OpenApiPathItem> path)
{
    var functions = new StringBuilder();

    foreach (var operation in path.Value.Operations)
    {
        functions.Append(JavaScriptFunction(name, path, operation));
    }

    return functions.ToString();
}

string JavaScriptFunction(string name, KeyValuePair<string, OpenApiPathItem> path,
    KeyValuePair<OperationType, OpenApiOperation> operation)
{
    return $@"{JSDoc(operation.Value)}
{DeclareFunction(name, path.Key, operation)}";
}

string DeclareFunction(string name, string pathKey,
    KeyValuePair<OperationType, OpenApiOperation> operation)
{
    return $@"export async function {name}({FunctionParameters(operation.Value.Parameters, operation.Value.RequestBody)}) {{
{DeclareUrl(pathKey, operation.Value.Parameters)}
{DeclareResponse(operation.Key, operation.Value.RequestBody)}
{HandleResponseError()}
{HandleNoContent(operation.Value.Responses)}
{HandleBody(operation.Value.Responses)}
}}";
}

string HandleNoContent(OpenApiResponses responses)
{
    if (!responses.ContainsKey("204"))
    {
        return string.Empty;
    }

    return @"
  if (response.status == 204) {
    return null;
  }";
}

string HandleBody(OpenApiResponses responses)
{
    const string nullResponse = "  return null;";

    if (!responses.TryGetValue("200", out OpenApiResponse? successResponse))
    {
        return nullResponse;
    }

    if (successResponse.Content.TryGetValue("application/json", out OpenApiMediaType? jsonTypeResponse))
    {
        string type = jsonTypeResponse.Schema.Reference?.Id ?? ConvertToTypeScript(jsonTypeResponse.Schema);

        switch (type)
        {
            case "string":
                {
                    return @"
  const body = await response.text();
  return body;";
                }

            case "number":
            case "integer":
                {
                    return @"
  const body = await response.text();
  return +body;";
                }

            case "boolean":
                {
                    return @"
  const body = await response.text();
  return /^true$/i.test(body);";
                }

            default:
                {
                    return @"
  const body = await response.json();
  return body;";
                }
        }
    }

    return nullResponse;
}

string DeclareResponse(OperationType operationType, OpenApiRequestBody? requestBody)
{

    if (requestBody?.Content?.TryGetValue("application/json", out OpenApiMediaType? _) == true)
    {
        return @$"
  const response = await fetch(url, {{
    method: '{operationType.ToString().ToUpper()}',
    body: JSON.stringify(body)
  }});";
    }

    return @$"
  const response = await fetch(url, {{
    method: '{operationType.ToString().ToUpper()}'
  }});";
}

string HandleResponseError()
{
    return @"
  if (!response.ok) {
    const message = await extractErrorMessage(response);
    throw new Error(message);
  }";
}

string FunctionParameters(IEnumerable<OpenApiParameter> parameters, OpenApiRequestBody? requestBody)
{
    var names = parameters.Select(x => x.Name).ToList();
    if (requestBody?.Content?.TryGetValue("application/json", out OpenApiMediaType? _) == true)
    {
        names.Add("body");
    }

    return string.Join(", ", names);
}

string DeclareUrl(string pathKey, IList<OpenApiParameter> parameters)
{
    if (!parameters.Any())
    {
        return $"  const url = `${{BASE_URL}}{pathKey}`;";
    }

    var queryParameters = parameters
        .Where(x => x.In == ParameterLocation.Query)
        .Select(x => $"{x.Name}: `${{{x.Name}}}`");

    string qs = string.Join(", ", queryParameters);

    return $@"  const qs = new URLSearchParams({{{qs}}});
  const url = `${{BASE_URL}}{pathKey}?${{qs}}`;";
}

string JSDoc(OpenApiOperation operation)
{
    return $@"
/**
 * {operation.Description}
 * {JSDocParameters(operation.Parameters, operation.RequestBody)}
 * {JSDocReturn(operation.Responses)}
 */";
}

string JSDocParameters(IEnumerable<OpenApiParameter> parameters, OpenApiRequestBody? requestBody)
{
    var p = parameters.Select(x =>
    {
        var type = $"{ConvertToTypeScript(x.Schema)}{NullableSchema(x.Schema)}";
        return $"@param {{{type}}} {x.Name} {JSDocParamDescription(x.Description)}";
    }).ToList();

    if (requestBody?.Content?.TryGetValue("application/json", out OpenApiMediaType? bodyContent) == true)
    {
        string nullable = requestBody.Required ? "?" : string.Empty;
        var type = $"{ConvertToTypeScript(bodyContent.Schema)}{nullable}";
        p.Add($"@param {{{type}}} body {JSDocParamDescription(requestBody.Description)}");
    }

    return string.Join(Environment.NewLine + " * ", p);
}

string JSDocParamDescription(string description)
{
    return string.IsNullOrEmpty(description) ? string.Empty : $"- {description}";
}

string NullableSchema(OpenApiSchema schema)
{
    return schema.Nullable ? "?" : string.Empty;
}

string NullableResponses(OpenApiResponses responses)
{
    return responses.Any(x => x.Key == "204" || x.Value.Content == null) ? "?" : string.Empty;
}

string ConvertToTypeScript(OpenApiSchema schema)
{
    string type = schema.Reference?.Id ?? schema.Type;
    return type switch
    {
        "integer" => "number",
        "array" => $"{ConvertToTypeScript(schema.Items)}[]",
        _ => type
    };
}

string JSDocReturn(OpenApiResponses responses)
{
    const string nullResponse = "@returns {Promise<null>}";

    if (!responses.TryGetValue("200", out OpenApiResponse? successResponse))
    {
        return nullResponse;
    }

    if (successResponse.Content == null)
    {
        return nullResponse;
    }

    string nullable = NullableResponses(responses);

    if (successResponse.Content.TryGetValue("application/json", out OpenApiMediaType? jsonTypeResponse))
    {
        string type = jsonTypeResponse.Schema.Reference?.Id ?? ConvertToTypeScript(jsonTypeResponse.Schema);
        string description = JSDocParamDescription(jsonTypeResponse.Schema.Description);
        if (type is "string" or "number" or "integer" or "boolean")
        {
            return $"@returns {{Promise<{type}{nullable}>}} {description}";
        }
        else
        {
            return $"@returns {{Promise<import('./Models').{type}{nullable}>}} {description}";
        }
    }

    return nullResponse;
}

string JavaScriptFileHeader()
{
    return @"import extractErrorMessage from '~/utils/extract-error-message';

const DOTNET_PORT = process.env['DOTNET_PORT'];
const BASE_URL = `http://localhost:${DOTNET_PORT}`;
";
}

string ToCamelCase(string original)
{
    var invalidCharsRgx = new Regex("[^_a-zA-Z0-9]");
    var whiteSpace = new Regex(@"(?<=\s)");
    var startsWithUpperCaseChar = new Regex("^[A-Z]");
    var firstCharFollowedByUpperCasesOnly = new Regex("(?<=[A-Z])[A-Z0-9]+$");
    var lowerCaseNextToNumber = new Regex("(?<=[0-9])[a-z]");
    var upperCaseInside = new Regex("(?<=[A-Z])[A-Z]+?((?=[A-Z][a-z])|(?=[0-9]))");

    // replace white spaces with underscore, then replace all invalid chars with empty string
    var camelCase = invalidCharsRgx.Replace(whiteSpace.Replace(original, "_"), string.Empty)
        // split by underscores
        .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
        // set first letter to uppercase
        .Select(w => startsWithUpperCaseChar.Replace(w, m => m.Value.ToLower()))
        // replace second and all following upper case letters to lower if there is no next lower (ABC -> Abc)
        .Select(w => firstCharFollowedByUpperCasesOnly.Replace(w, m => m.Value.ToLower()))
        // set upper case the first lower case following a number (Ab9cd -> Ab9Cd)
        .Select(w => lowerCaseNextToNumber.Replace(w, m => m.Value.ToUpper()))
        // lower second and next upper case letters except the last if it follows by any lower (ABcDEf -> AbcDef)
        .Select(w => upperCaseInside.Replace(w, m => m.Value.ToLower()));

    return string.Concat(camelCase);
}