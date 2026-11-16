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

        var content = new StackPanel { Margin = Ui.PageMargin };

        var general = new StackPanel();
        general.Children.Add(Ui.T("Arranque do Windows", "H2"));
        _startup = new CheckBox
        {
            Content = "Iniciar o HyperCore ao entrar no Windows",
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
            "A tarefa de arranque inicia o HyperCore com as permissões necessárias. Com a bandeja ativa, fechar a janela mantém a aplicação na área de notificação; usa o menu do ícone para sair.",
            "MutedText", 12));

        _status = Ui.T("", "MutedText", 12);
        _status.Margin = new Thickness(0, 12, 0, 0);
        general.Children.Add(_status);
        content.Children.Add(Ui.Card(general));
        var updates = UpdatesCard();
        updates.Margin = new Thickness(0, 14, 0, 0);
        content.Children.Add(updates);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>Atualizações: versão atual, automático ou perguntar, e procurar agora.</summary>
    private Border UpdatesCard()
    {
        var box = new StackPanel();
        box.Children.Add(Ui.T("Atualizações", "H2"));
        var state = Ui.T("", "MutedText", 12);
        state.Margin = new Thickness(0, 4, 0, 8);
        box.Children.Add(state);
        void RefreshState()
        {
            var s = AppServices.Settings;
            string last = s.UpdateLastCheckUtc is DateTime d ? $" · última verificação {d.ToLocalTime():dd/MM HH:mm}" : "";
            state.Text = Updater.Available is { } a
                ? $"Tens a v{Updater.Current.ToString(3)} — a v{a.Version.ToString(3)} já está disponível{last}."
                : $"Tens a v{Updater.Current.ToString(3)}{last}.";
        }
        RefreshState();
        Updater.AvailableChanged += () => state.Dispatcher.BeginInvoke(RefreshState);

        var auto = new RadioButton
        {
            Content = "Atualizar sozinho — instala as versões novas e reabre (nunca a meio de um jogo, scan ou Modo Jogo)",
            GroupName = "upd", Margin = new Thickness(0, 4, 0, 4), IsChecked = AppServices.Settings.UpdateMode == UpdateFlow.ModeAuto,
        };
        var manual = new RadioButton
        {
            Content = "Perguntar-me antes — avisa quando há uma versão nova e és tu que decides",
            GroupName = "upd", Margin = new Thickness(0, 4, 0, 4), IsChecked = AppServices.Settings.UpdateMode == UpdateFlow.ModeManual,
        };
        void SetMode(string mode)
        {
            AppServices.Settings.UpdateMode = mode;
            try { AppServices.Settings.Save(); } catch { }
        }
        auto.Checked += (_, _) => SetMode(UpdateFlow.ModeAuto);
        manual.Checked += (_, _) => SetMode(UpdateFlow.ModeManual);
        box.Children.Add(auto);
        box.Children.Add(manual);

        var check = Ui.Btn("Procurar atualizações agora", (_, _) => { }, "BtnAccent");
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Margin = new Thickness(0, 12, 0, 0);
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            check.Content = "A procurar…";
            try { await UpdateFlow.CheckAsync(userAsked: true, this); }
            finally
            {
                check.IsEnabled = true;
                check.Content = "Procurar atualizações agora";
                auto.IsChecked = AppServices.Settings.UpdateMode == UpdateFlow.ModeAuto;
                manual.IsChecked = AppServices.Settings.UpdateMode == UpdateFlow.ModeManual;
                RefreshState();
            }
        };
        box.Children.Add(check);
        return Ui.Card(box);
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
                ? "O HyperCore iniciará quando entrares no Windows."
                : "O HyperCore foi removido do arranque do Windows.";
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
                ? "O HyperCore iniciará na área de notificação."
                : "O HyperCore iniciará com a janela visível.";
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
