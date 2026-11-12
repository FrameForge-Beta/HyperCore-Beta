using System.Windows;
using System.Windows.Controls;
using FrameForge.Core;

namespace FrameForge.Pages;

public sealed class SettingsPage : UserControl
{
    private readonly Action<bool> _setTrayMode;
    private readonly TextBlock _status;
    private readonly CheckBox _startup;
    private readonly CheckBox _startMinimized;
    private bool _loading;

    public SettingsPage(Action<bool> setTrayMode)
    {
        _setTrayMode = setTrayMode;
        Ui.UseTheme(this);

        var content = new StackPanel { Margin = new Thickness(28) };
        content.Children.Add(Ui.T("Configurações", "H1"));
        content.Children.Add(Ui.T("Preferências da aplicação", "MutedText", 13));

        var general = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        general.Children.Add(Ui.T("Arranque do Windows", "H2"));
        _startup = new CheckBox
        {
            Content = "Iniciar o FrameForge ao entrar no Windows",
            IsChecked = AppServices.Settings.StartWithWindows,
            Margin = new Thickness(0, 8, 0, 5),
        };
        _startup.Checked += (_, _) => SetStartup(true);
        _startup.Unchecked += (_, _) => SetStartup(false);
        general.Children.Add(_startup);

        _startMinimized = new CheckBox
        {
            Content = "Iniciar minimizado na bandeja e manter a aplicação na bandeja ao fechar",
            IsChecked = AppServices.Settings.StartMinimizedToTray,
            Margin = new Thickness(0, 5, 0, 5),
        };
        _startMinimized.Checked += (_, _) => SetStartMinimized(true);
        _startMinimized.Unchecked += (_, _) => SetStartMinimized(false);
        general.Children.Add(_startMinimized);
        general.Children.Add(Ui.T(
            "A tarefa de arranque inicia o FrameForge com as permissões necessárias. Com a bandeja ativa, fechar a janela mantém a aplicação na área de notificação; usa o menu do ícone para sair.",
            "MutedText", 12));

        _status = Ui.T("", "MutedText", 12);
        _status.Margin = new Thickness(0, 12, 0, 0);
        general.Children.Add(_status);
        content.Children.Add(Ui.Card(general));
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void SetStartup(bool enabled)
    {
        if (_loading) return;
        bool previous = AppServices.Settings.StartWithWindows;
        AppServices.Settings.StartWithWindows = enabled;
        try
        {
            WindowsStartupRegistration.Update(enabled, AppServices.Settings.StartMinimizedToTray);
            AppServices.Settings.SaveOrThrow();
            _startup.IsChecked = enabled;
            _status.Text = enabled
                ? "O FrameForge iniciará quando entrares no Windows."
                : "O FrameForge foi removido do arranque do Windows.";
        }
        catch (Exception ex)
        {
            AppServices.Settings.StartWithWindows = previous;
            string rollbackError = "";
            try { WindowsStartupRegistration.Update(previous, AppServices.Settings.StartMinimizedToTray); }
            catch (Exception rollback) { rollbackError = $" A reversão também falhou: {rollback.Message}"; }
            _loading = true;
            _startup.IsChecked = previous;
            _loading = false;
            _status.Text = "Não foi possível alterar o arranque do Windows: " + ex.Message + rollbackError;
        }
    }

    private void SetStartMinimized(bool minimized)
    {
        if (_loading) return;
        bool previous = AppServices.Settings.StartMinimizedToTray;
        AppServices.Settings.StartMinimizedToTray = minimized;
        try
        {
            _setTrayMode(minimized);
            if (AppServices.Settings.StartWithWindows)
                WindowsStartupRegistration.Update(enabled: true, startMinimized: minimized);
            AppServices.Settings.SaveOrThrow();
            _status.Text = minimized
                ? "O FrameForge iniciará na área de notificação."
                : "O FrameForge iniciará com a janela visível.";
        }
        catch (Exception ex)
        {
            AppServices.Settings.StartMinimizedToTray = previous;
            string rollbackError = "";
            if (AppServices.Settings.StartWithWindows)
            {
                try { WindowsStartupRegistration.Update(enabled: true, startMinimized: previous); }
                catch (Exception rollback) { rollbackError = $" A reversão também falhou: {rollback.Message}"; }
            }
            try { _setTrayMode(previous); }
            catch (Exception rollback) { rollbackError += $" A reversão da bandeja também falhou: {rollback.Message}"; }
            _loading = true;
            _startMinimized.IsChecked = previous;
            _loading = false;
            _status.Text = "Não foi possível alterar a opção da área de notificação: " + ex.Message + rollbackError;
        }
    }
}
