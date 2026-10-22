using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OpenTapo.Tests.Fakes;

/// <summary>The Tapo smart API behind any transport fake (KLAP, AES, SSL): get/set_device_info, multipleRequest, other calls recorded.</summary>
internal abstract class FakeSmartDevice
{
    /// <summary>What get_device_info returns; set_device_info merges into it.</summary>
    public JsonObject Info { get; set; } = new() { ["device_on"] = false, ["model"] = "P110", ["type"] = "SMART.TAPOPLUG" };

    /// <summary>Other getters answered in multipleRequest: a null value is answered with an error code, a missing one too.</summary>
    public Dictionary<string, JsonNode?> Results { get; } = [];

    public ConcurrentQueue<(string Method, JsonNode? Params)> Calls { get; } = new();

    /// <summary>error_code returned by every call other than get_device_info and multipleRequest.</summary>
    public int ErrorCode { get; set; }

    protected JsonObject Process(string method, JsonNode? parameters)
    {
        if (method == "multipleRequest")
        {
            JsonArray responses = [];
            foreach (JsonNode? request in parameters!["requests"]!.AsArray())
            {
                string name = (string)request!["method"]!;
                JsonObject response = new() { ["method"] = name };
                if (name == "get_device_info")
                {
                    response["result"] = Info.DeepClone();
                    response["error_code"] = 0;
                }
                else if (Results.TryGetValue(name, out JsonNode? result) && result is not null)
                {
                    response["result"] = result.DeepClone();
                    response["error_code"] = 0;
                }
                else
                {
                    response["error_code"] = -40210;
                }

                responses.Add(response);
            }

            return new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject { ["responses"] = responses } };
        }

        Calls.Enqueue((method, parameters?.DeepClone()));
        if (method == "get_device_info")
        {
            return new JsonObject { ["error_code"] = 0, ["result"] = Info.DeepClone() };
        }

        if (method == "set_device_info" && ErrorCode == 0)
        {
            foreach ((string key, JsonNode? value) in parameters!.AsObject())
            {
                Info[key] = value?.DeepClone();
            }
        }

        return new JsonObject { ["error_code"] = ErrorCode };
    }
}
