using Models;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

string fileName = args[0];
await using FileStream openStream = File.OpenRead(fileName);
var swagger = (await JsonSerializer.DeserializeAsync<JsonObject>(openStream, options))!;
JsonObject? modules = swagger["paths"]?.AsObject();
if (modules == null) return;

var modulesCache = new Dictionary<string, JavaScriptModule>();

foreach ((string path, JsonNode? moduleInfo) in modules)
{
    if (moduleInfo == null) continue;

    string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2) continue;

    string moduleName = parts[0];

    if (!modulesCache.TryGetValue(moduleName, out JavaScriptModule? module))
    {
        module = new JavaScriptModule(moduleName);
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

var modelsCache = new List<JavaScriptModel>();

foreach ((string modelName, JsonNode? modelInfo) in models)
{
    if (modelInfo == null) continue;

    var type = modelInfo["type"]?.GetValue<string>();
    if (type != "object") continue;

    var model = new JavaScriptModel(modelName);

    JsonObject properties = modelInfo["properties"]?.AsObject() ?? new JsonObject();
    foreach ((string propertyName, JsonNode? propertyInfo) in properties)
    {
        var propertySchema = propertyInfo?.Deserialize<Schema>(options);
        string? typescriptType = propertySchema?.TypeScriptType;
        if (typescriptType == null) continue;

        model.Properties.Add(propertyName, typescriptType);
    }

    modelsCache.Add(model);
}

Directory.CreateDirectory(args[1]);

Debug.WriteLine("Writing Module files...");
{
    const string moduleHeader = @"const DOTNET_PORT = process.env['DOTNET_PORT'];
const BASE_URL = `http://localhost:${{DOTNET_PORT}}`;

";
    foreach ((string _, JavaScriptModule? module) in modulesCache)
    {
        string sourceCode = moduleHeader + module.ToJavaScript();
        if (Environment.NewLine == "\r\n")
        {
            sourceCode = sourceCode.Replace("\r", string.Empty);
        }

        await File.WriteAllTextAsync(Path.Join(args[1], $"{module.Name}.server.js"), sourceCode);
    }
}

Debug.WriteLine("Writing Models.d.ts...");
{
    string sourceCode = string.Join(Environment.NewLine, modelsCache.Select(x => x.ToJavaScript()));
    if (Environment.NewLine == "\r\n")
    {
        sourceCode = sourceCode.Replace("\r", string.Empty);
    }
    await File.WriteAllTextAsync(Path.Join(args[1], "Models.d.ts"), sourceCode);
}

Debug.WriteLine($"Done. {args[1]}");

namespace Models
{
    public record JavaScriptModule(string Name)
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
                source.AppendLine(ReturnResponse(function.ResponseModelName != null));
                source.AppendLine(CloseBody());
                source.AppendLine();
            }

            return source.ToString();
        }

        private static string OpenBody() => "{";

        private static string CloseBody() => "}";

        private static string ReturnResponse(bool hasResponse)
        {
            var sb = new StringBuilder();
            if (hasResponse)
            {
                sb.AppendLine("  const body = await response.json();");
                sb.AppendLine("  if (response.ok) return body;");
                sb.AppendLine();
                sb.Append("  throw body;");
            }
            else
            {
                sb.AppendLine("  if (response.ok) return;");
                sb.AppendLine();
                sb.AppendLine("  const body = await response.json();");
                sb.Append("  throw body;");
            }

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

        private static string DeclareUrl(string path, QueryParameter[]? queryParameters)
        {
            if (queryParameters?.Any() != true)
            {
                return $"  const url = `${{BASE_URL}}{path}`;";
            }

            string parameterNames = string.Join(", ", queryParameters.Select(x => $"{x.Name}: `${{{x.Name}}}`"));
            var sb = new StringBuilder();
            sb.AppendLine($"  const qs = new URLSearchParams({{ {parameterNames} }});");
            sb.AppendLine($"  const url = `${{BASE_URL}}{path}?${{qs}}`;");
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

    public record QueryParameter(string Name, Schema Schema);

    public record Schema(string Type)
    {
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

    public record JavaScriptModel(string ModelName)
    {
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
}