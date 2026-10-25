namespace CorePilot.ViewModels;

public enum ApplyChoice { Cancel, WriteOnly, WriteAndRestart }

/// <summary>Modal confirmation surface; implemented by the main window overlay.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);
    Task AlertAsync(string title, string message);
    Task<ApplyChoice> ConfirmApplyAsync(string deviceName, string changeSummary, bool canRestart);
}
