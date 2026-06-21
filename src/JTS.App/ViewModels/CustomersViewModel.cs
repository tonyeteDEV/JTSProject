using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Data.Entities;
using JTS_App.Services;

namespace JTS_App.ViewModels;

public partial class CustomersViewModel : ObservableObject
{
    private readonly DataverseAppDataService _data;

    public ObservableCollection<Customer> Customers { get; } = new();

    [ObservableProperty]
    private Customer? _selectedCustomer;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isSyncing;

    public CustomersViewModel(DataverseAppDataService data)
    {
        _data = data;
    }

    public async Task LoadAsync(bool forceSync = false)
    {
        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        Customers.Clear();
        foreach (var customer in snapshot.Projects
            .Select(p => p.Customer)
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c!.Name))
            .GroupBy(c => c!.DataverseId)
            .Select(g => g.First()!)
            .OrderBy(c => c.Name))
        {
            Customers.Add(customer);
        }

        Status = Customers.Count == 0
            ? "No customers found. Link customers to projects in Dataverse."
            : $"{Customers.Count} customer(s) from Dataverse.";
    }

    public async Task<Customer> AddAsync(string name, string? contactInfo, string? notes)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("Customers are managed in Dataverse.");
    }

    public async Task UpdateAsync(Customer customer)
    {
        await Task.CompletedTask;
        Status = "Customers are managed in Dataverse.";
    }

    public async Task DeleteAsync(Customer customer)
    {
        await Task.CompletedTask;
        throw new NotSupportedException("Customers are managed in Dataverse.");
    }

    public async Task SyncFromD365Async()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        Status = "Syncing D365CE...";
        try
        {
            await LoadAsync();
            Status = "Customers loaded from Dataverse.";
        }
        catch (Exception ex)
        {
            Status = $"D365CE sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
