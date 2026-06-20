using JTS.Mobile.Pages;

namespace JTS.Mobile;

public partial class AppShell : Shell
{
    public AppShell(TasksPage tasks, FocusPage focus, AgentPage agent, SettingsPage settings)
    {
        InitializeComponent();
        TasksContent.Content = tasks;
        FocusContent.Content = focus;
        AgentContent.Content = agent;
        SettingsContent.Content = settings;
    }
}
