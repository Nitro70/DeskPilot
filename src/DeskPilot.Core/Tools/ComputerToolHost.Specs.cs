using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Tools;

public sealed partial class ComputerToolHost
{
    /// <summary>
    /// The tool list from docs/DESIGN.md, filtered by the safety settings. Descriptions state the coordinate
    /// space and whether action tools return a fresh screenshot, both read from settings at call time.
    /// Schemas avoid keywords some providers reject (additionalProperties, minimum/maximum, default).
    /// </summary>
    public IReadOnlyList<ToolSpec> GetTools()
    {
        AppSettings s;
        try { s = _settings() ?? new AppSettings(); }
        catch (Exception) { s = new AppSettings(); }

        bool normalized = s.Screen.Coordinates == CoordinateMode.Normalized1000;
        bool vision = s.ActiveProfile?.SupportsVision ?? true;

        string coords = normalized
            ? "Coordinates are on a 0-1000 scale across the full screenshot on both axes (0,0 = top-left corner, 1000,1000 = bottom-right corner), whatever the real resolution."
            : "Coordinates are pixels of the full screenshot image you were shown (0,0 = top-left corner), not physical screen pixels; DeskPilot converts them.";
        string after = !s.Screen.ScreenshotAfterAction
            ? " Returns a short text result; call screenshot to see the effect."
            : vision
                ? " Returns a fresh screenshot taken just after the action, so you do not need to call screenshot again."
                : " Returns a fresh text description of the screen taken just after the action.";
        string xDesc = normalized ? "X coordinate on the 0-1000 scale (0 = left edge, 1000 = right edge)." : "X coordinate in screenshot pixels (0 = left edge).";
        string yDesc = normalized ? "Y coordinate on the 0-1000 scale (0 = top edge, 1000 = bottom edge)." : "Y coordinate in screenshot pixels (0 = top edge).";
        string space = normalized ? "0-1000 coordinates" : "screenshot pixels";

        var tools = new List<ToolSpec>
        {
            Spec("screenshot",
                vision
                    ? "Take a screenshot of the screen DeskPilot controls. Returns the image plus one line with its size, the active window and the mouse position. " + coords
                    : "Describe the screen as text (this model cannot see images): the open windows and the interactive UI elements of the active window, with center coordinates you can click. " + coords,
                Schema()),

            Spec("zoom",
                "Return a sharper, higher-resolution image of one region of the screen, for reading small text or icons. " +
                "The region is given in the coordinate space of the full screenshot. " + coords +
                " Clicks and other actions must still use full-screenshot coordinates, never coordinates inside the zoom image.",
                Schema(
                    ("x", Num($"Left edge of the region ({space})."), true),
                    ("y", Num($"Top edge of the region ({space})."), true),
                    ("width", Num($"Width of the region ({space}), greater than 0."), true),
                    ("height", Num($"Height of the region ({space}), greater than 0."), true))),

            Spec("click",
                "Move the mouse to a point and click it: left, right or middle button, 1-3 clicks, optional modifier keys held during the click. " + coords + after,
                Schema(
                    ("x", Num(xDesc), true),
                    ("y", Num(yDesc), true),
                    ("button", Enum("Mouse button: left (default), right (context menu) or middle.", "left", "right", "middle"), false),
                    ("clicks", Int("Number of clicks: 1 (default), 2 = double-click, 3 = triple-click."), false),
                    ("modifiers", EnumArray("Keys held during the click, e.g. [\"ctrl\"] for ctrl+click. Any of ctrl, shift, alt, win.", "ctrl", "shift", "alt", "win"), false))),

            Spec("move_mouse",
                "Move the mouse pointer to a point without clicking (for hover menus and tooltips). " + coords + after,
                Schema(("x", Num(xDesc), true), ("y", Num(yDesc), true))),

            Spec("drag",
                "Drag with a mouse button held: press at the start point, move smoothly to the end point, release. For sliders, selecting text, moving files or windows. " + coords + after,
                Schema(
                    ("from_x", Num("Start point X. " + xDesc), true),
                    ("from_y", Num("Start point Y. " + yDesc), true),
                    ("to_x", Num("End point X. " + xDesc), true),
                    ("to_y", Num("End point Y. " + yDesc), true),
                    ("button", Enum("Mouse button to hold: left (default), right or middle.", "left", "right", "middle"), false))),

            Spec("scroll",
                "Scroll with the mouse wheel. If x and y are given the mouse moves there first; otherwise whatever is under the mouse now scrolls. " + coords + after,
                Schema(
                    ("direction", Enum("Direction to scroll: up, down, left or right.", "up", "down", "left", "right"), true),
                    ("amount", Int("Number of wheel notches, 1-30 (default 3). One notch is about three lines of text."), false),
                    ("x", Num("Optional point to scroll at. " + xDesc + " Give x and y together."), false),
                    ("y", Num("Optional point to scroll at. " + yDesc + " Give x and y together."), false))),

            Spec("type_text",
                $"Type text into the focused control as if typed on the keyboard (any Unicode). A newline character (\\n) presses Enter. At most {MaxTypeTextLength} characters per call. Click into the target field first." + after,
                Schema(
                    ("text", Str($"The text to type, at most {MaxTypeTextLength} characters. \\n presses Enter."), true),
                    ("press_enter", Bool("Press Enter after typing (default false), e.g. to submit a search box."), false))),

            Spec("press_keys",
                "Press one key or key combination, such as \"ctrl+s\", \"enter\", \"alt+tab\", \"win\", \"ctrl+shift+esc\", \"f5\" or \"pagedown\". One combination per call; use repeat to press it several times." + after,
                Schema(
                    ("keys", Str("One key combination: modifiers (ctrl, shift, alt, win) and at most one other key joined with +, e.g. \"ctrl+c\"."), true),
                    ("repeat", Int("How many times to press it, 1-50 (default 1)."), false))),

            Spec("wait",
                vision
                    ? "Wait for the screen to settle (app launches, page loads, animations), then return a fresh screenshot."
                    : "Wait for the screen to settle (app launches, page loads, animations), then return a fresh text description of the screen.",
                Schema(("seconds", Num("How long to wait, 0.1-30 seconds."), true))),

            Spec("list_windows",
                $"List the open top-level windows, front to back: title, process, position and size ({space}), and flags such as active, minimized, elevated or on another monitor.",
                Schema()),

            Spec("focus_window",
                "Bring a window to the front and give it the keyboard focus (restores it when minimized). Matches the title case-insensitively: " +
                "the exact title first, then titles starting with the text, then titles containing it; a process name such as \"notepad\" also works." + after,
                Schema(("title", Str("Window title or part of it (case-insensitive), or a process name."), true))),
        };

        if (s.Safety.AllowAppLaunch)
            tools.Add(Spec("launch",
                "Open an app, file, folder or URL the way Windows does (like the Run dialog or a double-click), e.g. \"notepad\", \"calc\", " +
                "\"C:\\\\Users\\\\Public\\\\file.txt\", \"https://example.com\", \"ms-settings:display\" or a Start menu app name. " +
                (s.Safety.AllowAdmin ? "Runs normally, not as administrator unless the program itself asks." : "Never runs as administrator.") +
                (s.Screen.ScreenshotAfterAction ? after.Replace("just after the action", "after giving the app time to open") : after),
                Schema(
                    ("target", Str("What to open: an app name, a program on PATH, a full path, a folder, or a URL."), true),
                    ("arguments", Str("Optional command-line arguments for a program."), false))));

        tools.Add(Spec("ui_elements",
            "List the interactive UI elements (buttons, fields, links, menu items...) of a window through UI Automation: control type, name, " +
            $"center point and size ({space}), disabled state. Use it to find exact click targets. Defaults to the active window.",
            Schema(
                ("window_title", Str("Optional window title (or part of it, or a process name). Default: the active window."), false),
                ("filter", Str("Optional text; only elements whose name or control type contains it are listed."), false),
                ("max", Int("Maximum number of elements to list, 1-200 (default 80)."), false))));

        if (s.Safety.AllowClipboard)
        {
            tools.Add(Spec("get_clipboard", "Read the text currently on the clipboard.", Schema()));
            tools.Add(Spec("set_clipboard",
                "Put text on the clipboard, for example to paste long text with press_keys \"ctrl+v\".",
                Schema(("text", Str("The text to put on the clipboard."), true))));
        }

        if (s.Safety.AllowShellCommands)
            tools.Add(Spec("run_command",
                (OperatingSystem.IsWindows()
                    ? "Run a PowerShell or cmd command in a hidden window, not as administrator, "
                    : "Run a bash or sh command without a terminal window, not as root, ") +
                "with the user's home folder as working directory, and return " +
                $"its exit code and output (each stream truncated to about {OutputStreamLimit / 1000} KB). Not interactive: commands that wait for input time out.",
                Schema(
                    ("command", Str("The command line to run."), true),
                    ("shell", Enum($"Shell: {ComputerToolHost.ShellNames[0]} (default) or {ComputerToolHost.ShellNames[1]}.", ComputerToolHost.ShellNames.ToArray()), false),
                    ("timeout_seconds", Int("Time limit in seconds, 1-600 (default 60)."), false))));

        return tools;
    }

    private static ToolSpec Spec(string name, string description, JsonObject schema) =>
        ToolSpec.Create(name, description, schema.ToJsonString());

    private static JsonObject Schema(params (string Name, JsonObject Property, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, property, isRequired) in properties)
        {
            props[name] = property;
            if (isRequired) required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject Enum(string description, params string[] values)
    {
        var list = new JsonArray();
        foreach (var v in values) list.Add(v);
        return new JsonObject { ["type"] = "string", ["enum"] = list, ["description"] = description };
    }

    private static JsonObject EnumArray(string description, params string[] values) => new()
    {
        ["type"] = "array",
        ["items"] = Enum("A modifier key.", values),
        ["description"] = description,
    };
}
