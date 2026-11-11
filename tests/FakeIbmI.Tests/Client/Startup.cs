// Startup Response Record codes (RFC 4777 section 10.4).

namespace FakeIbmI.Tests.Client;

public sealed record StartupResult(string Code, string SystemName, string DeviceName, bool Ok, string Message);

public static class StartupCodes
{
    private static readonly Dictionary<string, string> Success = new()
    {
        ["I901"] = "Session started; the device has less function than the source device",
        ["I902"] = "Session started",
        ["I906"] = "Automatic sign-on not allowed; the sign-on screen follows",
    };

    private static readonly Dictionary<string, string> Failure = new()
    {
        ["2702"] = "Device description not found",
        ["2703"] = "Controller description not found",
        ["2777"] = "Damaged device description",
        ["8901"] = "Device not varied on",
        ["8902"] = "Device not available (in use by another session)",
        ["8903"] = "Device not valid for this session",
        ["8906"] = "Session initiation failed",
        ["8907"] = "Session failure",
        ["8910"] = "Controller not valid for this session",
        ["8916"] = "No matching device found",
        ["8917"] = "Not authorized to the device",
        ["8918"] = "Job cancelled",
        ["8920"] = "Device partially damaged",
        ["8921"] = "Communications error",
        ["8922"] = "Negative response received",
        ["8923"] = "Start-up record built incorrectly",
        ["8925"] = "Creation of the device failed",
        ["8928"] = "Change of the device failed",
        ["8929"] = "Vary on or vary off failed",
        ["8930"] = "Message queue does not exist",
        ["8934"] = "Start-up for S/36 WSF received",
        ["8935"] = "Session rejected",
        ["8936"] = "Security failure on session attempt",
        ["8937"] = "Automatic sign-on rejected",
        ["8940"] = "The IBM i could not create a new device (automatic configuration failed or is not allowed)",
        ["I904"] = "Source system at an incompatible release",
    };

    public const string DeviceInUseCode = "8902";

    public static StartupResult Describe(StartupResponse response)
    {
        var ok = Success.TryGetValue(response.Code, out var success);
        var message = ok
            ? success!
            : Failure.TryGetValue(response.Code, out var failure)
                ? failure
                : $"Unknown startup response {response.Code}";
        return new StartupResult(response.Code, response.SystemName, response.DeviceName, ok, message);
    }
}
