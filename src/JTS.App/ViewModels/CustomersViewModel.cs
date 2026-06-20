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
        await _data.LoadTaskSnapshotAsync(forceSync);
        Customers.Clear();
        Status = "Customers are loaded through project references in Dataverse.";
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
