using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Agent;

// STUB: owned by the Session module agent.
public static class ProfileAutoConfig
{
    /// <summary>Adds profiles for providers found on this PC (Ollama, LM Studio, API keys in env, Gemini CLI) that are not configured yet. Returns true if anything changed.</summary>
    public static bool ApplyDetected(AppSettings settings, EnvironmentReport report) => throw new NotImplementedException("STUB");
}
