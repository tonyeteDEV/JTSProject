using System.Collections.Generic;
using JTS.Core;
using JTS.Data.Entities;
using Microsoft.UI.Xaml.Controls;

namespace JTS_App.Dialogs;

public sealed partial class ProjectRelationDialog : ContentDialog
{
    public Project? SelectedProject => ProjectBox.SelectedItem as Project;
    public ProjectRelationType RelationType => RelationTypeBox.SelectedItem is ProjectRelationType t ? t : ProjectRelationType.RelatedTo;
    public string? Note => string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text;

    public ProjectRelationDialog()
    {
        InitializeComponent();
        RelationTypeBox.ItemsSource = Enum.GetValues<ProjectRelationType>();
        RelationTypeBox.SelectedIndex = 0;
    }

    public void SetCandidates(IEnumerable<Project> candidates)
    {
        ProjectBox.ItemsSource = candidates.ToList();
    }
}
