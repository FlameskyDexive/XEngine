using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XHub.ViewModels;

namespace XHub.Views;

public partial class ProjectsPage : UserControl
{
    public ProjectsPage()
    {
        InitializeComponent();
        AddHandler(DoubleTappedEvent, OnListDoubleTapped, RoutingStrategies.Bubble);
    }

    private ProjectsViewModel? Vm => DataContext as ProjectsViewModel;

    /// <summary>Enter 打开 (§2.2 keyboard flow): a focused row opens with the keyboard.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Return || Vm == null) return;
        // Let the search box keep Enter (no-op there); open only from row focus.
        if (IsInside(e.Source as Visual, static a => a is TextBox)) return;
        ListBox? list = this.FindControl<ListBox>("ProjectList");
        if (list?.SelectedItem is ProjectItemViewModel item)
        {
            Vm.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm == null) return;
        Visual? source = e.Source as Visual;
        if (source == null) return;
        foreach (Visual ancestor in source.GetVisualAncestors())
        {
            if (ancestor is TextBox) return; // double-tap in the search box
            if (ancestor is ListBoxItem { DataContext: ProjectItemViewModel item })
            {
                Vm.OpenCommand.Execute(item);
                return;
            }
        }
    }

    private bool IsInside(Visual? source, Func<Visual, bool> predicate)
    {
        if (source == null) return false;
        foreach (Visual ancestor in source.GetVisualAncestors())
            if (predicate(ancestor)) return true;
        return false;
    }
}
