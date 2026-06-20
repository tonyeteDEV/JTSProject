using System.Globalization;
using JTS.Data.Entities;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace JTS_App.Services;

public sealed class DataverseTimesheetService
{
    private const string TimesheetTable = "jts_hojadehoras";
    private const string TimesheetStart = "jts_inicio";
    private const string TimesheetEnd = "jts_final";
    private const string TimesheetTotalHours = "jts_horastotales";
    private const string TimesheetBillableHours = "jts_horasfacturables";
    private const string TimesheetIncidentHours = "jts_horasincidencias";
    private const string TimesheetInternalHours = "jts_horasinternas";
    private const string TimesheetHoursByDay = "jts_horaspordia";

    private const string TimesheetLineTable = "jts_lineadehoras";
    private const string TimesheetLineSheet = "jts_hojadehoras";
    private const string TimesheetLineProject = "jts_proyecto";
    private const string TimesheetLineCustomer = "jts_cliente";
    private const string TimesheetLineType = "jts_tipo";
    private const string TimesheetLineTotalHours = "jts_horastotales";
    private static readonly IReadOnlyDictionary<DayOfWeek, (string Hours, string Comments)> LineDayFields =
        new Dictionary<DayOfWeek, (string Hours, string Comments)>
        {
            [DayOfWeek.Monday] = ("jts_lunes", "jts_comentarioslunes"),
            [DayOfWeek.Tuesday] = ("jts_martes", "jts_comentariosmartes"),
            [DayOfWeek.Wednesday] = ("jts_miercoles", "jts_comentariosmiercoles"),
            [DayOfWeek.Thursday] = ("jts_jueves", "jts_comentariosjueves"),
            [DayOfWeek.Friday] = ("jts_viernes", "jts_comentariosviernes"),
            [DayOfWeek.Saturday] = ("jts_sabado", "jts_comentariossabado"),
            [DayOfWeek.Sunday] = ("jts_domingo", "jts_comentariosdomingo")
        };

    private const string ProjectTable = "jts_proyecto";
    private const string ProjectCustomer = "jts_cliente";
    private const string TimeTable = "jts_tiempotarea";
    private const string CommentTable = "jts_comentariotarea";

    private readonly AppSettingsService _settings;

    public DataverseTimesheetService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public async Task<TimesheetRegistrationResult> CreateAsync(TimesheetRegistrationDraft draft)
    {
        var options = await GetOptionsAsync();
        if (!options.IsComplete)
            throw new InvalidOperationException("Complete tenant, client, secret, and environment URL in Settings before creating a timesheet.");

        using var service = CreateServiceClient(options);
        var timeMetadata = RetrieveMetadata(service, TimeTable);
        var timeLineLookup = FindLookupAttribute(timeMetadata, TimesheetLineTable);
        if (timeLineLookup is null)
            throw new InvalidOperationException("Missing the Timesheet line lookup on jts_tiempotarea. Deploy the JTS_Base solution first.");
        var commentMetadata = RetrieveMetadata(service, CommentTable);
        var commentLineLookup = FindLookupAttribute(commentMetadata, TimesheetLineTable);
        if (commentLineLookup is null)
            throw new InvalidOperationException("Missing the Timesheet line lookup on jts_comentariotarea. Deploy the JTS_Base solution first.");

        var customerByProjectId = await LoadProjectCustomersAsync(service, draft.Lines.Select(l => l.ProjectDataverseId).Distinct());

        var sheetIds = new List<Guid>();
        var lineResults = new List<TimesheetLineRegistrationResult>();

        foreach (var weekGroup in draft.Lines
            .GroupBy(l => StartOfWeek(l.Date))
            .OrderBy(g => g.Key))
        {
            var weekStart = weekGroup.Key;
            var weekEnd = weekStart.AddDays(6);
            var sheetId = await GetOrCreateDraftSheetAsync(service, weekStart, weekEnd);
            sheetIds.Add(sheetId);

            foreach (var projectGroup in weekGroup.GroupBy(l => new { l.ProjectDataverseId, l.ProjectName }))
            {
                var entity = new Entity(TimesheetLineTable);
                entity[TimesheetLineSheet] = new EntityReference(TimesheetTable, sheetId);
                entity[TimesheetLineProject] = new EntityReference(ProjectTable, projectGroup.Key.ProjectDataverseId);
                if (!customerByProjectId.TryGetValue(projectGroup.Key.ProjectDataverseId, out var customer))
                    throw new InvalidOperationException($"The project \"{projectGroup.Key.ProjectName}\" has no customer assigned in Dataverse.");
                entity[TimesheetLineCustomer] = customer;
                entity[TimesheetLineType] = new OptionSetValue(0);
                entity[TimesheetLineTotalHours] = projectGroup.Sum(l => l.Hours);

                foreach (var dayGroup in projectGroup.GroupBy(l => l.Date.DayOfWeek))
                {
                    if (!LineDayFields.TryGetValue(dayGroup.Key, out var fields)) continue;
                    entity[fields.Hours] = dayGroup.Sum(l => l.Hours);
                    entity[fields.Comments] = string.Join(Environment.NewLine, dayGroup.Select(l => l.Summary).Where(s => !string.IsNullOrWhiteSpace(s)));
                }

                var lineId = await Task.Run(() => service.Create(entity));
                foreach (var timeEntryId in projectGroup.SelectMany(l => l.TimeEntryDataverseIds).Distinct())
                {
                    var update = new Entity(TimeTable, timeEntryId)
                    {
                        [timeLineLookup.LogicalName] = new EntityReference(TimesheetLineTable, lineId)
                    };
                    await Task.Run(() => service.Update(update));
                }

                foreach (var commentId in projectGroup.SelectMany(l => l.CommentDataverseIds).Distinct())
                {
                    var update = new Entity(CommentTable, commentId)
                    {
                        [commentLineLookup.LogicalName] = new EntityReference(TimesheetLineTable, lineId)
                    };
                    await Task.Run(() => service.Update(update));
                }

                foreach (var line in projectGroup)
                    lineResults.Add(new TimesheetLineRegistrationResult(line.LocalKey, lineId));
            }

            await UpdateSheetTotalsAsync(service, sheetId);
        }

        return new TimesheetRegistrationResult(sheetIds.First(), lineResults);
    }

    private async Task<D365Options> GetOptionsAsync() => new(
        await _settings.GetD365TenantIdAsync() ?? string.Empty,
        await _settings.GetD365ClientIdAsync() ?? string.Empty,
        await _settings.GetD365ClientSecretAsync() ?? string.Empty,
        await _settings.GetD365EnvironmentUrlAsync() ?? string.Empty);

    private static ServiceClient CreateServiceClient(D365Options options)
    {
        var client = new ServiceClient(
            $"AuthType=ClientSecret;Url={options.NormalizedEnvironmentUrl};ClientId={options.ClientId};ClientSecret={options.ClientSecret};TenantId={options.TenantId};RequireNewInstance=true");
        if (!client.IsReady)
            throw new InvalidOperationException(client.LastError ?? "Dataverse client is not ready.");
        return client;
    }

    private static EntityMetadata RetrieveMetadata(ServiceClient service, string logicalName)
    {
        var response = (RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
        {
            LogicalName = logicalName,
            EntityFilters = EntityFilters.Attributes,
            RetrieveAsIfPublished = true
        });
        return response.EntityMetadata;
    }

    private static async Task<Dictionary<Guid, EntityReference>> LoadProjectCustomersAsync(ServiceClient service, IEnumerable<Guid> projectIds)
    {
        var ids = projectIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, EntityReference>();

        var rows = await RetrieveAllAsync(service, new QueryExpression(ProjectTable)
        {
            ColumnSet = new ColumnSet(ProjectCustomer),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("jts_proyectoid", ConditionOperator.In, ids.Cast<object>().ToArray())
                }
            }
        });

        return rows
            .Select(row => new { ProjectId = row.Id, Customer = row.GetAttributeValue<EntityReference>(ProjectCustomer) })
            .Where(row => row.Customer is not null)
            .ToDictionary(row => row.ProjectId, row => row.Customer!);
    }

    private static async Task<Guid> GetOrCreateDraftSheetAsync(ServiceClient service, DateTime weekStart, DateTime weekEnd)
    {
        var existing = await RetrieveAllAsync(service, new QueryExpression(TimesheetTable)
        {
            ColumnSet = new ColumnSet("jts_hojadehorasid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(TimesheetStart, ConditionOperator.Equal, weekStart.Date),
                    new ConditionExpression(TimesheetEnd, ConditionOperator.Equal, weekEnd.Date),
                    new ConditionExpression("statecode", ConditionOperator.Equal, 0)
                }
            },
            Orders = { new OrderExpression("createdon", OrderType.Descending) },
            PageInfo = new PagingInfo { Count = 1, PageNumber = 1 }
        });

        if (existing.Count > 0) return existing[0].Id;

        var sheet = new Entity(TimesheetTable)
        {
            [TimesheetStart] = weekStart.Date,
            [TimesheetEnd] = weekEnd.Date,
            [TimesheetTotalHours] = 0m,
            [TimesheetBillableHours] = 0m,
            [TimesheetIncidentHours] = 0m,
            [TimesheetInternalHours] = 0m,
            [TimesheetHoursByDay] = BuildHoursByDayText(new Dictionary<DayOfWeek, decimal>())
        };

        return await Task.Run(() => service.Create(sheet));
    }

    private static async Task UpdateSheetTotalsAsync(ServiceClient service, Guid sheetId)
    {
        var dayFields = LineDayFields.ToDictionary(pair => pair.Key, pair => pair.Value.Hours);
        var columns = new ColumnSet(new[] { TimesheetLineTotalHours, TimesheetLineType }.Concat(dayFields.Values).ToArray());
        var lines = await RetrieveAllAsync(service, new QueryExpression(TimesheetLineTable)
        {
            ColumnSet = columns,
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(TimesheetLineSheet, ConditionOperator.Equal, sheetId)
                }
            }
        });

        var total = lines.Sum(line => ReadDecimal(line, TimesheetLineTotalHours));
        var billable = SumByLineType(lines, 0);
        var incidents = SumByLineType(lines, 1);
        var internalHours = SumByLineType(lines, 2);
        var hoursByDay = dayFields.ToDictionary(
            pair => pair.Key,
            pair => lines.Sum(line => ReadDecimal(line, pair.Value)));

        var update = new Entity(TimesheetTable, sheetId)
        {
            [TimesheetTotalHours] = total,
            [TimesheetBillableHours] = billable,
            [TimesheetIncidentHours] = incidents,
            [TimesheetInternalHours] = internalHours,
            [TimesheetHoursByDay] = BuildHoursByDayText(hoursByDay)
        };
        await Task.Run(() => service.Update(update));
    }

    private static AttributeMetadata? FindLookupAttribute(EntityMetadata metadata, string targetLogicalName) =>
        metadata.Attributes
            .OfType<LookupAttributeMetadata>()
            .FirstOrDefault(a => a.Targets?.Contains(targetLogicalName, StringComparer.OrdinalIgnoreCase) == true);

    private static string? FindDateAttribute(EntityMetadata metadata, params string[] candidates) =>
        FindAttribute(metadata, AttributeTypeCode.DateTime, candidates)?.LogicalName;

    private static AttributeMetadata? FindNumberAttribute(EntityMetadata metadata, params string[] candidates)
    {
        var numericTypes = new[]
        {
            AttributeTypeCode.Decimal,
            AttributeTypeCode.Double,
            AttributeTypeCode.Integer,
            AttributeTypeCode.Money
        };
        return FindAttribute(metadata, numericTypes, candidates);
    }

    private static string? FindTextAttribute(EntityMetadata metadata, params string[] candidates) =>
        FindAttribute(metadata, new[] { AttributeTypeCode.String, AttributeTypeCode.Memo }, candidates)?.LogicalName;

    private static AttributeMetadata? FindAttribute(EntityMetadata metadata, AttributeTypeCode type, params string[] candidates) =>
        FindAttribute(metadata, new[] { type }, candidates);

    private static AttributeMetadata? FindAttribute(EntityMetadata metadata, IReadOnlyCollection<AttributeTypeCode> types, params string[] candidates)
    {
        var attributes = metadata.Attributes
            .Where(a => a.LogicalName is not null && a.AttributeType is AttributeTypeCode type && types.Contains(type))
            .ToList();

        foreach (var candidate in candidates)
        {
            var exact = attributes.FirstOrDefault(a => string.Equals(a.LogicalName, candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        foreach (var candidate in candidates.Select(NormalizeName))
        {
            var partial = attributes.FirstOrDefault(a => NormalizeName(a.LogicalName).Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (partial is not null) return partial;
        }

        return null;
    }

    private static void SetText(Entity entity, string? attribute, string? value)
    {
        if (!string.IsNullOrWhiteSpace(attribute))
            entity[attribute] = value ?? string.Empty;
    }

    private static void SetDate(Entity entity, string? attribute, DateTime value)
    {
        if (!string.IsNullOrWhiteSpace(attribute))
            entity[attribute] = value.Date;
    }

    private static void SetNumber(Entity entity, AttributeMetadata? attribute, decimal value)
    {
        if (attribute?.LogicalName is not { Length: > 0 } logicalName) return;

        entity[logicalName] = attribute.AttributeType switch
        {
            AttributeTypeCode.Decimal => value,
            AttributeTypeCode.Double => (double)value,
            AttributeTypeCode.Integer => (int)Math.Ceiling(value),
            AttributeTypeCode.Money => new Money(value),
            _ => value
        };
    }

    private static string NormalizeName(string value) =>
        value.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

    private static string BuildHoursByDayText(IReadOnlyList<TimesheetLineRegistrationDraft> lines)
    {
        var hoursByDay = lines
            .GroupBy(line => line.Date.DayOfWeek)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Hours));

        return BuildHoursByDayText(hoursByDay);
    }

    private static string BuildHoursByDayText(IReadOnlyDictionary<DayOfWeek, decimal> hoursByDay)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"L: {FormatDayHours(hoursByDay.GetValueOrDefault(DayOfWeek.Monday))}",
            $"M: {FormatDayHours(hoursByDay.GetValueOrDefault(DayOfWeek.Tuesday))}",
            $"M: {FormatDayHours(hoursByDay.GetValueOrDefault(DayOfWeek.Wednesday))}",
            $"J: {FormatDayHours(hoursByDay.GetValueOrDefault(DayOfWeek.Thursday))}",
            $"V: {FormatDayHours(hoursByDay.GetValueOrDefault(DayOfWeek.Friday))}",
            $"S: {FormatDayHours(hoursByDay.GetValueOrDefault(DayOfWeek.Saturday))}",
            $"D: {FormatDayHours(hoursByDay.GetValueOrDefault(DayOfWeek.Sunday))}"
        });
    }

    private static string FormatDayHours(decimal hours) =>
        hours.ToString("0.0", CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(Entity entity, string attribute)
    {
        if (!entity.Attributes.TryGetValue(attribute, out var value) || value is null) return 0m;
        return value switch
        {
            decimal d => d,
            double d => (decimal)d,
            int i => i,
            Money money => money.Value,
            _ => 0m
        };
    }

    private static decimal SumByLineType(IEnumerable<Entity> lines, int typeValue) =>
        lines
            .Where(line => line.GetAttributeValue<OptionSetValue>(TimesheetLineType)?.Value == typeValue)
            .Sum(line => ReadDecimal(line, TimesheetLineTotalHours));

    private static DateTime StartOfWeek(DateTime value)
    {
        var offset = ((int)value.DayOfWeek + 6) % 7;
        return value.Date.AddDays(-offset);
    }

    private static async Task<List<Entity>> RetrieveAllAsync(ServiceClient service, QueryExpression query)
    {
        var rows = new List<Entity>();
        query.PageInfo = new PagingInfo { Count = 500, PageNumber = 1 };
        while (true)
        {
            var page = await Task.Run(() => service.RetrieveMultiple(query));
            rows.AddRange(page.Entities);
            if (!page.MoreRecords) break;
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        }

        return rows;
    }
}

public sealed record TimesheetRegistrationDraft(
    string Name,
    DateTime StartDate,
    DateTime EndDate,
    decimal TotalHours,
    IReadOnlyList<TimesheetLineRegistrationDraft> Lines);

public sealed record TimesheetLineRegistrationDraft(
    string LocalKey,
    Guid ProjectDataverseId,
    string ProjectName,
    DateTime Date,
    decimal Hours,
    string Summary,
    IReadOnlyList<Guid> TimeEntryDataverseIds,
    IReadOnlyList<Guid> CommentDataverseIds);

public sealed record TimesheetRegistrationResult(
    Guid TimesheetDataverseId,
    IReadOnlyList<TimesheetLineRegistrationResult> Lines);

public sealed record TimesheetLineRegistrationResult(string LocalKey, Guid DataverseId);
