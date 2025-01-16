using MavlinkInspector.Controls;
using System.Windows;

namespace MavlinkInspector.Services;

public static class MessageBoxService
{
    public static MessageBoxResult ShowError(string message, Window? owner = null, string? title = null)
    {
        return ModernMessageBox.ShowMessage(
            message,
            title ?? "Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error,
            owner);
    }

    public static void ShowError(Exception ex, Window? owner = null)
    {
        ShowError(ex.Message, owner, "Error Occurred");
    }

    public static MessageBoxResult ShowWarning(string message, Window? owner = null, string? title = null)
    {
        return ModernMessageBox.ShowMessage(
            message,
            title ?? "Warning",
            MessageBoxButton.OK,
            MessageBoxImage.Warning,
            owner);
    }

    public static MessageBoxResult ShowInfo(string message, Window? owner = null, string? title = null)
    {
        return ModernMessageBox.ShowMessage(
            message,
            title ?? "Information",
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            owner);
    }

    public static MessageBoxResult ShowQuestion(string message, Window? owner = null, string? title = null)
    {
        return ModernMessageBox.ShowMessage(
            message,
            title ?? "Question",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            owner);
    }

    public static MessageBoxResult ShowConfirm(string message, Window? owner = null, string? title = null)
    {
        return ModernMessageBox.ShowMessage(
            message,
            title ?? "Confirm",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            owner);
    }

    public static void ShowSuccess(string message, Window? owner = null, string? title = null)
    {
        ModernMessageBox.ShowMessage(
            message,
            title ?? "Success",
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            owner);
    }

    public static void ShowNotification(string message, Window? owner = null)
    {
        ModernMessageBox.ShowMessage(
            message,
            "Notification",
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            owner);
    }

    public static bool AskConfirmation(string message, Window? owner = null)
    {
        return ModernMessageBox.ShowMessage(
            message,
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            owner) == MessageBoxResult.Yes;
    }
}
