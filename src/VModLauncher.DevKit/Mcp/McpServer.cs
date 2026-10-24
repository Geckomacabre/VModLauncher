using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VModLauncher.DevKit.Mcp;

/// <summary>A tool the server offers: its JSON Schema, and the code that runs it and returns text.</summary>
public sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema, Func<JsonObject, string> Run, bool ReadOnly = true);

/// <summary>Thrown by a tool for a problem the caller should fix (bad arguments, a missing file): reported as the tool's error.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// A Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0), with no dependencies: initialize,
/// ping, tools/list and tools/call. Requests run one at a time, in order. Nothing but protocol messages goes to the
/// output stream; anything else a tool prints should go to stderr.
/// </summary>
public sealed class McpServer
{
    public const string Name = "vmod-devkit";
    static readonly string[] Supported = { "2025-06-18", "2025-03-26", "2024-11-05" };

    readonly IReadOnlyList<McpTool> tools;
    readonly string version;
    readonly string instructions;

    public McpServer(IReadOnlyList<McpTool> tools, string version, string instructions)
    {
        this.tools = tools;
        this.version = version;
        this.instructions = instructions;
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancel = default)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        await using var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        string? line;
        while ((line = await reader.ReadLineAsync(cancel)) != null)
        {
            if (line.Trim().Length == 0) continue;
            var reply = HandleLine(line);
            if (reply != null) await writer.WriteLineAsync(reply.ToJsonString());
        }
    }

    /// <summary>One incoming line to its reply (null for notifications and replies to us).</summary>
    public JsonNode? HandleLine(string line)
    {
        JsonNode? message;
        try { message = JsonNode.Parse(line); }
        catch (JsonException ex) { return Error(null, -32700, "Parse error: " + ex.Message); }
        if (message is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var m in batch)
                if (m is JsonObject o && Handle(o) is { } r) replies.Add(r);
            return replies.Count > 0 ? replies : null;
        }
        return message is JsonObject obj ? Handle(obj) : Error(null, -32600, "Invalid request");
    }

    JsonObject? Handle(JsonObject msg)
    {
        var id = msg["id"]?.DeepClone();
        bool notification = !msg.ContainsKey("id");
        var method = msg["method"]?.GetValue<string>();
        if (method == null) return null; // a response to something we sent: we send nothing
        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var asked = msg["params"]?["protocolVersion"]?.GetValue<string>();
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = asked != null && Supported.Contains(asked) ? asked : Supported[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = Name, ["title"] = "V Mod Launcher dev kit", ["version"] = version },
                        ["instructions"] = instructions,
                    });
                }
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = new JsonArray(tools.Select(Describe).ToArray<JsonNode?>()) });
                case "tools/call":
                    return Result(id, Call(msg["params"] as JsonObject));
                case "resources/list":
                    return Result(id, new JsonObject { ["resources"] = new JsonArray() });
                case "prompts/list":
                    return Result(id, new JsonObject { ["prompts"] = new JsonArray() });
                default:
                    return notification ? null : Error(id, -32601, "Method not found: " + method);
            }
        }
        catch (Exception ex)
        {
            return notification ? null : Error(id, -32603, ex.Message);
        }
    }

    static JsonObject Describe(McpTool t) => new()
    {
        ["name"] = t.Name,
        ["title"] = t.Title,
        ["description"] = t.Description,
        ["inputSchema"] = t.InputSchema.DeepClone(),
        ["annotations"] = new JsonObject { ["title"] = t.Title, ["readOnlyHint"] = t.ReadOnly, ["destructiveHint"] = false, ["openWorldHint"] = false },
    };

    JsonObject Call(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new ArgumentException("tools/call needs a name");
        var tool = tools.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException("Unknown tool: " + name);
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        foreach (var req in (tool.InputSchema["required"] as JsonArray ?? new JsonArray()).Select(r => r!.GetValue<string>()))
            if (args[req] == null) return ToolError($"{name}: \"{req}\" is required");
        string text;
        try { text = tool.Run(args); }
        catch (Exception ex) when (ex is ToolException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ToolError(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolError(ex.GetType().Name + ": " + ex.Message);
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = false,
        };
    }

    static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };

    static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
