using System.Globalization;
using System.Windows.Input;
using DeskPilot.Core.Abstractions;
using DeskPilot.Mvvm;

namespace DeskPilot.ViewModels;

/// <summary>What the floating overlay shows while a turn runs.</summary>
public sealed class OverlayViewModel : ObservableModel
{
    private string _title = "Working";
    private string _detail = "Thinking…";
    private int _steps;
    private string _hotkeyHint = "";
    private bool _dryRun;
    private bool _isStopping;

    public OverlayViewModel(Action stop)
    {
        StopCommand = new ActionCommand(stop);
    }

    public ICommand StopCommand { get; }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public bool DryRun { get => _dryRun; set => SetProperty(ref _dryRun, value); }
    public bool IsStopping { get => _isStopping; private set => SetProperty(ref _isStopping, value); }

    public int Steps
    {
        get => _steps;
        private set
        {
            if (SetProperty(ref _steps, value)) OnPropertyChanged(nameof(FooterText));
        }
    }

    /// <summary>e.g. "Ctrl+Alt+X". Empty when no hotkey is active.</summary>
    public string HotkeyHint
    {
        get => _hotkeyHint;
        set
        {
            if (SetProperty(ref _hotkeyHint, value ?? "")) OnPropertyChanged(nameof(FooterText));
        }
    }

    public string FooterText
    {
        get
        {
            var step = Steps == 0 ? "No actions yet" : Steps == 1 ? "1 action" : $"{Steps.ToString(CultureInfo.InvariantCulture)} actions";
            return HotkeyHint.Length == 0 ? step : $"{step}  ·  {HotkeyHint} to stop";
        }
    }

    public void BeginTurn()
    {
        Steps = 0;
        Detail = "Thinking…";
        IsStopping = false;
    }

    public void OnState(AgentState state)
    {
        IsStopping = state == AgentState.Stopping;
        Title = state switch
        {
            AgentState.Starting => "Starting",
            AgentState.Stopping => "Stopping",
            AgentState.Error => "Error",
            AgentState.Idle => "Done",
            _ => "DeskPilot is working",
        };
    }

    public void OnEvent(AgentEvent e)
    {
        switch (e)
        {
            case ToolCallEvent call:
                Steps++;
                Detail = string.IsNullOrWhiteSpace(call.Summary) ? call.ToolName : call.Summary;
                break;
            case ThinkingEvent:
                if (Steps == 0) Detail = "Thinking…";
                break;
            case AssistantTextEvent:
                Detail = "Writing a reply…";
                break;
            case StatusEvent { Level: not StatusLevel.Info } status:
                Detail = status.Message;
                break;
        }
    }
}
