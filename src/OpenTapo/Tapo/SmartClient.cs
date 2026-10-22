using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>The Tapo "smart" JSON API ({method, params} → result), whatever transport carries it.</summary>
public abstract class SmartClient
{
    public abstract Task<JsonNode> CallAsync(string method, JsonObject? parameters = null);

    /// <summary>Runs several getters in one round trip. Returns method → result, null for calls the device rejected or lacks.</summary>
    public virtual async Task<IReadOnlyDictionary<string, JsonNode?>> ExecuteAsync(params string[] methods)
    {
        JsonArray requests = new(methods.Select(method => (JsonNode)new JsonObject { ["method"] = method }).ToArray());
        JsonNode reply = await CallAsync("multipleRequest", new JsonObject { ["requests"] = requests });
        Dictionary<string, JsonNode?> results = [];
        foreach (JsonNode? response in reply["responses"]?.AsArray() ?? [])
        {
            if ((string?)response?["method"] is string method)
            {
                results[method] = (int?)response["error_code"] == 0 ? response["result"] : null;
            }
        }

        return results;
    }

    protected static JsonObject Request(string method, JsonObject? parameters)
    {
        JsonObject request = new() { ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        return request;
    }

    /// <summary>The "result" of a {error_code, result} reply.</summary>
    protected static JsonNode ResultOf(JsonNode reply, string method)
    {
        int errorCode;
        JsonNode? result;
        try
        {
            errorCode = (int?)reply["error_code"] ?? 0;
            result = reply["result"];
        }
        catch (Exception ex) when (TapoException.IsMalformedReply(ex))
        {
            throw new TapoException("Unexpected reply from the device.");
        }

        if (errorCode != 0)
        {
            throw new TapoException($"{method}: error {errorCode}");
        }

        return result ?? new JsonObject();
    }
}
