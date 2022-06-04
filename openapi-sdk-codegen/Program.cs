using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

string fileName = args[0];
await using FileStream openStream = File.OpenRead(fileName);
var swagger = (await JsonSerializer.DeserializeAsync<JsonObject>(openStream, options))!;
JsonObject? modules = swagger["paths"]?.AsObject();
if (modules == null) return;

var modulesCache = new Dictionary<string, Module>();

foreach ((string path, JsonNode? moduleInfo) in modules)
{
    if (moduleInfo == null) continue;

    string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2) continue;

    string moduleName = parts[0];

    if (!modulesCache.TryGetValue(moduleName, out Module? module))
    {
        module = new Module(moduleName);
        modulesCache.Add(moduleName, module);
    }

    string functionName = parts[1];

    foreach ((string functionHttpMethod, JsonNode? functionInfo) in moduleInfo.AsObject())
    {
        var queryParameters = functionInfo?["parameters"]?.Deserialize<QueryParameter[]>(options);
        var responseRef = functionInfo?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>();
        string? responseModelName = responseRef?.Split('/').Last();

        var moduleFunction = new ModuleFunction(functionName, queryParameters, responseModelName, functionHttpMethod, path);
        module.Functions.Add(moduleFunction);
    }
}

JsonObject? models = swagger["components"]?["schemas"]?.AsObject();
if (models == null) return;

var modelsCache = new List<Model>();

foreach ((string modelName, JsonNode? modelInfo) in models)
{
    if (modelInfo == null) continue;

    var type = modelInfo["type"]?.GetValue<string>();
    if (type != "object") continue;

    var model = new Model(modelName);

    JsonObject properties = modelInfo["properties"]?.AsObject() ?? new JsonObject();
    foreach ((string propertyName, JsonNode? propertyInfo) in properties)
    {
        var propertySchema = propertyInfo?.Deserialize<Schema>(options);
        var typescriptType = propertySchema?.TypeScriptType;
        if (typescriptType == null) continue;

        model.Properties.Add(propertyName, typescriptType);
    }

    modelsCache.Add(model);
}

foreach ((string _, Module? module) in modulesCache)
{
    await File.WriteAllTextAsync(Path.Join(args[1], $"{module.Name}.server.js"), module.ToJavaScript());
}

await File.WriteAllTextAsync(Path.Join(args[1], "models.d.ts"),
    string.Join(Environment.NewLine, modelsCache.Select(x => x.ToJavaScript())));

public record Module(string Name)
{
    public HashSet<ModuleFunction> Functions { get; } = new();

    public string ToJavaScript()
    {
        var source = new StringBuilder();
        foreach (ModuleFunction function in Functions)
        {
            if (function.QueryParameters?.Any() == true)
            {
                source.AppendLine(JsDoc(function.QueryParameters, function.ResponseModelName));
            }

            source.Append(DeclareFunction(function.Name, function.QueryParameters));
            source.AppendLine(OpenBody());
            source.AppendLine(DeclareUrl(function.Path, function.QueryParameters));
            source.AppendLine(FetchResponse());
            source.AppendLine(ReturnResponse());
            source.AppendLine(CloseBody());
        }

        return source.ToString();
    }

    private static string OpenBody() => "{";

    private static string CloseBody() => "}";

    private static string ReturnResponse()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  const body = await response.json();");
        sb.AppendLine("  if (response.ok) return body;");
        sb.AppendLine();
        sb.AppendLine("  throw body;");

        return sb.ToString();
    }

    private static string FetchResponse()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  const response = await fetch(url, {");
        sb.AppendLine("    headers: { 'Accept': 'application/json' }");
        sb.AppendLine("  });");
        return sb.ToString();
    }

    private static string DeclareUrl(string urlPath, QueryParameter[]? queryParameters)
    {
        if (queryParameters?.Any() != true)
        {
            return $"  const url = '{urlPath}';";
        }

        string parameterNames = string.Join(", ", queryParameters.Select(x => x.Name));
        var sb = new StringBuilder();
        sb.AppendLine($"  const qs = new URLSearchParams({{ {parameterNames} }});");
        sb.AppendLine($"  const url = `{urlPath}?${{qs.toString()}}`;");
        return sb.ToString();
    }

    private static string DeclareFunction(string functionName, QueryParameter[]? parameters)
    {
        var sb = new StringBuilder($"export async function {functionName}(");
        if (parameters?.Any() == true)
        {
            string parameterNames = string.Join(", ", parameters.Select(x => x.Name));
            sb.Append(parameterNames);
        }

        sb.Append(") ");
        return sb.ToString();
    }

    private static string JsDoc(IEnumerable<QueryParameter> parameters, string? responseModelName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"/**");
        sb.AppendJoin(Environment.NewLine, parameters.Select(x => $" * @param {{{x.Schema.TypeScriptType}}} {x.Name}"));
        sb.AppendLine();
        if (responseModelName != null)
        {
            sb.AppendLine($" * @returns {{Promise<import('./models').{responseModelName}>}}");
        }

        sb.Append(" */");
        return sb.ToString();
    }
}

public record ModuleFunction(string Name, QueryParameter[]? QueryParameters, string? ResponseModelName, string HttpMethod, string Path);

public class QueryParameter
{
    public string Name { get; set; }
    public Schema Schema { get; set; }
}

public class Schema
{
    public string Type { get; set; }

    public string TypeScriptType
    {
        get
        {
            return Type switch
            {
                "integer" => "number",
                _ => Type
            };
        }
    }
}

public class Model
{
    public Model(string modelName)
    {
        ModelName = modelName;
    }

    public string ModelName { get; }
    public Dictionary<string, string> Properties { get; } = new();

    public string ToJavaScript()
    {
        var source = new StringBuilder();
        source.Append(DeclareInterface(ModelName));
        source.AppendLine(OpenBody());
        source.AppendLine(DeclareProperties(Properties));
        source.AppendLine(CloseBody());
        return source.ToString();
    }

    private static string DeclareProperties(Dictionary<string, string> properties)
    {
        return string.Join(Environment.NewLine, properties.Select(x => $"  {x.Key}: {x.Value};"));
    }

    private static string OpenBody() => "{";

    private static string CloseBody() => "}";

    private static string DeclareInterface(string modelName)
    {
        return $"export interface {modelName} ";
    }
}