using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

const int SpanishLcid = 3082;
const string SolutionUniqueName = "JTS_Base";
const string EnvironmentUrl = "https://jtsolutions-dev.crm4.dynamics.com/";

var settings = new CredentialSettings();
var tenantId = settings.Get("D365TenantId") ?? throw new InvalidOperationException("Missing D365TenantId in Windows Credential Manager.");
var clientId = settings.Get("D365ClientId") ?? throw new InvalidOperationException("Missing D365ClientId in Windows Credential Manager.");
var clientSecret = settings.Get("D365ClientSecret") ?? throw new InvalidOperationException("Missing D365ClientSecret in Windows Credential Manager.");

using var service = new ServiceClient(
    $"AuthType=ClientSecret;Url={EnvironmentUrl};ClientId={clientId};ClientSecret={clientSecret};TenantId={tenantId};RequireNewInstance=true");

if (!service.IsReady)
    throw new InvalidOperationException(service.LastError ?? "Dataverse client is not ready.");

EnsureSolutionExists(service, SolutionUniqueName);

var created = new List<string>();
var existing = new List<string>();

EnsureEntity(
    service,
    "jts_VideoAnalisis",
    "jts_Nombre",
    "Análisis de vídeo",
    "Análisis de vídeos",
    "Nombre",
    created,
    existing);

EnsureEntity(
    service,
    "jts_VideoAnalisisSegmento",
    "jts_Nombre",
    "Segmento de análisis de vídeo",
    "Segmentos de análisis de vídeo",
    "Nombre",
    created,
    existing);

EnsureEntity(
    service,
    "jts_VideoAnalisisSegmentoTarea",
    "jts_Nombre",
    "Relación segmento-tarea",
    "Relaciones segmento-tarea",
    "Nombre",
    created,
    existing);

EnsureEntity(
    service,
    "jts_DocumentacionGenerada",
    "jts_Nombre",
    "Documentación generada",
    "Documentaciones generadas",
    "Nombre",
    created,
    existing);

EnsureEntity(
    service,
    "jts_ConocimientoProyecto",
    "jts_Nombre",
    "Conocimiento de proyecto",
    "Conocimientos de proyecto",
    "Nombre",
    created,
    existing);

EnsureVideoAnalisisFields(service, created, existing);
EnsureSegmentoFields(service, created, existing);
EnsureSegmentoTareaFields(service, created, existing);
EnsureDocumentacionFields(service, created, existing);
EnsureConocimientoFields(service, created, existing);

PublishAll(service);

Console.WriteLine("CREATED");
foreach (var item in created.OrderBy(x => x))
    Console.WriteLine("- " + item);

Console.WriteLine();
Console.WriteLine("EXISTING");
foreach (var item in existing.OrderBy(x => x))
    Console.WriteLine("- " + item);

static void EnsureVideoAnalisisFields(IOrganizationService service, List<string> created, List<string> existing)
{
    const string table = "jts_videoanalisis";
    EnsureLookup(service, table, "jts_ProyectoPrincipalId", "Proyecto principal", "jts_proyecto", created, existing);
    EnsureDateTime(service, table, "jts_FechaAnalisis", "Fecha de análisis", created, existing);
    EnsurePicklist(service, table, "jts_Estado", "Estado", [
        ("Pendiente", 100000000), ("Procesando", 100000001), ("Revisado", 100000002),
        ("Completado", 100000003), ("Error", 100000004)
    ], created, existing);
    EnsureString(service, table, "jts_UrlVideo", "Ruta o URL del vídeo", 4000, created, existing);
    EnsureInteger(service, table, "jts_DuracionSegundos", "Duración en segundos", 0, 100000000, created, existing);
    EnsureString(service, table, "jts_Idioma", "Idioma", 20, created, existing);
    EnsureString(service, table, "jts_ModeloUsado", "Modelo usado", 200, created, existing);
    EnsureMemo(service, table, "jts_TranscripcionCompleta", "Transcripción completa", created, existing);
    EnsureMemo(service, table, "jts_OcrVisualCompleto", "OCR visual completo", created, existing);
    EnsureMemo(service, table, "jts_ResumenGlobal", "Resumen global", created, existing);
    EnsureMemo(service, table, "jts_DocumentacionGlobal", "Documentación global", created, existing);
    EnsureMemo(service, table, "jts_ContextoUsado", "Contexto usado", created, existing);
    EnsureMemo(service, table, "jts_ResultadoJson", "Resultado JSON", created, existing);
}

static void EnsureSegmentoFields(IOrganizationService service, List<string> created, List<string> existing)
{
    const string table = "jts_videoanalisissegmento";
    EnsureLookup(service, table, "jts_VideoAnalisisId", "Análisis de vídeo", "jts_videoanalisis", created, existing);
    EnsureLookup(service, table, "jts_ProyectoDetectadoId", "Proyecto detectado", "jts_proyecto", created, existing);
    EnsureInteger(service, table, "jts_InicioSegundos", "Inicio en segundos", 0, 100000000, created, existing);
    EnsureInteger(service, table, "jts_FinSegundos", "Fin en segundos", 0, 100000000, created, existing);
    EnsurePicklist(service, table, "jts_TipoAccion", "Tipo de acción", [
        ("Configuración", 100000000), ("Desarrollo", 100000001), ("Prueba", 100000002),
        ("Documentación", 100000003), ("Error", 100000004), ("Decisión", 100000005), ("Otro", 100000006)
    ], created, existing);
    EnsureDecimal(service, table, "jts_Confianza", "Confianza", 0, 100, created, existing);
    EnsureMemo(service, table, "jts_TextoAudio", "Texto de audio", created, existing);
    EnsureMemo(service, table, "jts_OcrDetectado", "OCR detectado", created, existing);
    EnsureMemo(service, table, "jts_AccionDetectada", "Acción detectada", created, existing);
    EnsureMemo(service, table, "jts_ResumenSegmento", "Resumen del segmento", created, existing);
    EnsureString(service, table, "jts_FrameReferencia", "Frame de referencia", 4000, created, existing);
}

static void EnsureSegmentoTareaFields(IOrganizationService service, List<string> created, List<string> existing)
{
    const string table = "jts_videoanalisissegmentotarea";
    EnsureLookup(service, table, "jts_SegmentoId", "Segmento", "jts_videoanalisissegmento", created, existing);
    EnsureLookup(service, table, "jts_TareaId", "Tarea", "task", created, existing);
    EnsureDecimal(service, table, "jts_Confianza", "Confianza", 0, 100, created, existing);
    EnsurePicklist(service, table, "jts_Relevancia", "Relevancia", [
        ("Principal", 100000000), ("Secundaria", 100000001), ("Contexto", 100000002)
    ], created, existing);
    EnsureBoolean(service, table, "jts_ConfirmadoUsuario", "Confirmado por usuario", created, existing);
    EnsureMemo(service, table, "jts_MotivoAsociacion", "Motivo de asociación", created, existing);
}

static void EnsureDocumentacionFields(IOrganizationService service, List<string> created, List<string> existing)
{
    const string table = "jts_documentaciongenerada";
    EnsureLookup(service, table, "jts_VideoAnalisisId", "Análisis de vídeo", "jts_videoanalisis", created, existing);
    EnsureLookup(service, table, "jts_SegmentoId", "Segmento", "jts_videoanalisissegmento", created, existing);
    EnsureLookup(service, table, "jts_TareaId", "Tarea", "task", created, existing);
    EnsureLookup(service, table, "jts_ProyectoId", "Proyecto", "jts_proyecto", created, existing);
    EnsureLookup(service, table, "jts_ComentarioTareaId", "Comentario de tarea aplicado", "jts_comentariotarea", created, existing);
    EnsurePicklist(service, table, "jts_Tipo", "Tipo", [
        ("Comentario de tarea", 100000000), ("Documentación técnica", 100000001), ("Release notes", 100000002),
        ("Resumen", 100000003), ("Evidencia de prueba", 100000004), ("Conocimiento", 100000005)
    ], created, existing);
    EnsurePicklist(service, table, "jts_Estado", "Estado", [
        ("Borrador", 100000000), ("Aprobado", 100000001), ("Aplicado", 100000002), ("Descartado", 100000003)
    ], created, existing);
    EnsureDateTime(service, table, "jts_Fecha", "Fecha", created, existing);
    EnsureBoolean(service, table, "jts_GeneradoPorIa", "Generado por IA", created, existing);
    EnsureBoolean(service, table, "jts_RevisadoUsuario", "Revisado por usuario", created, existing);
    EnsureString(service, table, "jts_Fuente", "Fuente", 200, created, existing);
    EnsureMemo(service, table, "jts_ContenidoMarkdown", "Contenido Markdown", created, existing);
    EnsureMemo(service, table, "jts_EvidenciasJson", "Evidencias JSON", created, existing);
}

static void EnsureConocimientoFields(IOrganizationService service, List<string> created, List<string> existing)
{
    const string table = "jts_conocimientoproyecto";
    EnsureLookup(service, table, "jts_ProyectoId", "Proyecto", "jts_proyecto", created, existing);
    EnsureLookup(service, table, "jts_VideoAnalisisId", "Análisis de vídeo", "jts_videoanalisis", created, existing);
    EnsureLookup(service, table, "jts_DocumentacionId", "Documentación generada", "jts_documentaciongenerada", created, existing);
    EnsurePicklist(service, table, "jts_Tipo", "Tipo", [
        ("Decisión", 100000000), ("Convención", 100000001), ("Entidad o campo", 100000002),
        ("Proceso", 100000003), ("Riesgo", 100000004), ("Otro", 100000005)
    ], created, existing);
    EnsureBoolean(service, table, "jts_Activo", "Activo", created, existing);
    EnsureDateTime(service, table, "jts_Fecha", "Fecha", created, existing);
    EnsureString(service, table, "jts_Fuente", "Fuente", 200, created, existing);
    EnsureMemo(service, table, "jts_Contenido", "Contenido", created, existing);
}

static void EnsureSolutionExists(IOrganizationService service, string uniqueName)
{
    var query = new QueryExpression("solution")
    {
        ColumnSet = new ColumnSet("solutionid"),
        Criteria = new FilterExpression
        {
            Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, uniqueName) }
        }
    };
    var result = service.RetrieveMultiple(query);
    if (result.Entities.Count == 0)
        throw new InvalidOperationException($"Solution '{uniqueName}' was not found.");
}

static void EnsureEntity(
    IOrganizationService service,
    string schemaName,
    string primaryNameSchema,
    string displayName,
    string collectionName,
    string primaryNameDisplay,
    List<string> created,
    List<string> existing)
{
    var logicalName = schemaName.ToLowerInvariant();
    if (EntityExists(service, logicalName))
    {
        existing.Add($"Tabla {logicalName}");
        AddToSolution(service, logicalName, 1);
        return;
    }

    var entity = new EntityMetadata
    {
        SchemaName = schemaName,
        DisplayName = Label(displayName),
        DisplayCollectionName = Label(collectionName),
        Description = Label(displayName),
        OwnershipType = OwnershipTypes.UserOwned,
        IsActivity = false
    };

    var primary = new StringAttributeMetadata
    {
        SchemaName = primaryNameSchema,
        DisplayName = Label(primaryNameDisplay),
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
        MaxLength = 200,
        FormatName = StringFormatName.Text
    };

    service.Execute(new CreateEntityRequest
    {
        Entity = entity,
        PrimaryAttribute = primary,
        HasNotes = false,
        HasActivities = false,
        SolutionUniqueName = SolutionUniqueName
    });
    created.Add($"Tabla {logicalName}");
}

static void EnsureString(IOrganizationService service, string table, string schemaName, string label, int maxLength, List<string> created, List<string> existing) =>
    EnsureAttribute(service, table, schemaName, label, () => new StringAttributeMetadata
    {
        SchemaName = schemaName,
        DisplayName = Label(label),
        MaxLength = maxLength,
        FormatName = StringFormatName.Text,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
    }, created, existing);

static void EnsureMemo(IOrganizationService service, string table, string schemaName, string label, List<string> created, List<string> existing) =>
    EnsureAttribute(service, table, schemaName, label, () => new MemoAttributeMetadata
    {
        SchemaName = schemaName,
        DisplayName = Label(label),
        MaxLength = 1048576,
        FormatName = MemoFormatName.TextArea,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
    }, created, existing);

static void EnsureInteger(IOrganizationService service, string table, string schemaName, string label, int min, int max, List<string> created, List<string> existing) =>
    EnsureAttribute(service, table, schemaName, label, () => new IntegerAttributeMetadata
    {
        SchemaName = schemaName,
        DisplayName = Label(label),
        MinValue = min,
        MaxValue = max,
        Format = IntegerFormat.None,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
    }, created, existing);

static void EnsureDecimal(IOrganizationService service, string table, string schemaName, string label, decimal min, decimal max, List<string> created, List<string> existing) =>
    EnsureAttribute(service, table, schemaName, label, () => new DecimalAttributeMetadata
    {
        SchemaName = schemaName,
        DisplayName = Label(label),
        MinValue = min,
        MaxValue = max,
        Precision = 2,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
    }, created, existing);

static void EnsureDateTime(IOrganizationService service, string table, string schemaName, string label, List<string> created, List<string> existing) =>
    EnsureAttribute(service, table, schemaName, label, () => new DateTimeAttributeMetadata
    {
        SchemaName = schemaName,
        DisplayName = Label(label),
        Format = DateTimeFormat.DateAndTime,
        DateTimeBehavior = DateTimeBehavior.UserLocal,
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
    }, created, existing);

static void EnsureBoolean(IOrganizationService service, string table, string schemaName, string label, List<string> created, List<string> existing) =>
    EnsureAttribute(service, table, schemaName, label, () => new BooleanAttributeMetadata
    {
        SchemaName = schemaName,
        DisplayName = Label(label),
        OptionSet = new BooleanOptionSetMetadata(
            new OptionMetadata(Label("Sí"), 1),
            new OptionMetadata(Label("No"), 0)),
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
    }, created, existing);

static void EnsurePicklist(IOrganizationService service, string table, string schemaName, string label, (string Label, int Value)[] options, List<string> created, List<string> existing) =>
    EnsureAttribute(service, table, schemaName, label, () =>
    {
        var optionSet = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
        foreach (var option in options)
            optionSet.Options.Add(new OptionMetadata(Label(option.Label), option.Value));

        return new PicklistAttributeMetadata
        {
            SchemaName = schemaName,
            DisplayName = Label(label),
            OptionSet = optionSet,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
        };
    }, created, existing);

static void EnsureLookup(IOrganizationService service, string table, string schemaName, string label, string targetTable, List<string> created, List<string> existing) =>
    EnsureLookupAttribute(service, table, schemaName, label, targetTable, created, existing);

static void EnsureLookupAttribute(
    IOrganizationService service,
    string table,
    string schemaName,
    string label,
    string targetTable,
    List<string> created,
    List<string> existing)
{
    var logicalName = schemaName.ToLowerInvariant();
    if (AttributeExists(service, table, logicalName))
    {
        existing.Add($"Campo {table}.{logicalName}");
        return;
    }

    service.Execute(new CreateOneToManyRequest
    {
        Lookup = new LookupAttributeMetadata
        {
            SchemaName = schemaName,
            DisplayName = Label(label),
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
        },
        OneToManyRelationship = new OneToManyRelationshipMetadata
        {
            SchemaName = RelationshipSchemaName(table, targetTable, logicalName),
            ReferencedEntity = targetTable,
            ReferencingEntity = table,
            AssociatedMenuConfiguration = new AssociatedMenuConfiguration
            {
                Behavior = AssociatedMenuBehavior.UseLabel,
                Group = AssociatedMenuGroup.Details,
                Label = Label(label),
                Order = 10000
            }
        },
        SolutionUniqueName = SolutionUniqueName
    });
    created.Add($"Campo {table}.{logicalName} ({label})");
}

static void EnsureAttribute(
    IOrganizationService service,
    string table,
    string schemaName,
    string label,
    Func<AttributeMetadata> create,
    List<string> created,
    List<string> existing)
{
    var logicalName = schemaName.ToLowerInvariant();
    if (AttributeExists(service, table, logicalName))
    {
        existing.Add($"Campo {table}.{logicalName}");
        return;
    }

    service.Execute(new CreateAttributeRequest
    {
        EntityName = table,
        Attribute = create(),
        SolutionUniqueName = SolutionUniqueName
    });
    created.Add($"Campo {table}.{logicalName} ({label})");
}

static bool EntityExists(IOrganizationService service, string logicalName)
{
    try
    {
        service.Execute(new RetrieveEntityRequest
        {
            LogicalName = logicalName,
            EntityFilters = EntityFilters.Entity,
            RetrieveAsIfPublished = true
        });
        return true;
    }
    catch
    {
        return false;
    }
}

static bool AttributeExists(IOrganizationService service, string table, string logicalName)
{
    try
    {
        service.Execute(new RetrieveAttributeRequest
        {
            EntityLogicalName = table,
            LogicalName = logicalName,
            RetrieveAsIfPublished = true
        });
        return true;
    }
    catch
    {
        return false;
    }
}

static void AddToSolution(IOrganizationService service, string componentLogicalName, int componentType)
{
    try
    {
        service.Execute(new AddSolutionComponentRequest
        {
            ComponentType = componentType,
            ComponentId = GetComponentId(service, componentLogicalName, componentType),
            SolutionUniqueName = SolutionUniqueName,
            AddRequiredComponents = false,
            DoNotIncludeSubcomponents = false
        });
    }
    catch
    {
        // Existing solution membership is fine for this idempotent setup.
    }
}

static Guid GetComponentId(IOrganizationService service, string logicalName, int componentType)
{
    if (componentType == 1)
    {
        var response = (RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
        {
            LogicalName = logicalName,
            EntityFilters = EntityFilters.Entity,
            RetrieveAsIfPublished = true
        });
        return response.EntityMetadata.MetadataId!.Value;
    }

    throw new NotSupportedException($"Unsupported component type {componentType}.");
}

static void PublishAll(IOrganizationService service) =>
    service.Execute(new PublishAllXmlRequest());

static Label Label(string text) => new(text, SpanishLcid);

static string RelationshipSchemaName(string referencingTable, string referencedTable, string lookupLogicalName)
{
    var baseName = $"jts_{referencingTable.Replace("jts_", "")}_{referencedTable.Replace("jts_", "")}_{lookupLogicalName.Replace("jts_", "")}";
    return baseName.Length <= 95 ? baseName : baseName[..95];
}

internal sealed class CredentialSettings
{
    private const string Prefix = "JTS.App.Settings.";
    private const int CredentialTypeGeneric = 1;

    public string? Get(string key)
    {
        if (!CredRead(Prefix + key, CredentialTypeGeneric, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}
