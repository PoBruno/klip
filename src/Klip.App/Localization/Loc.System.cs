namespace Klip.App.Localization;

// strings for startup, tray and diagnostics
public static partial class Loc
{
    // ----- Privacy: secrets kept for a while -----
    public static string SecretKeepTitle => Get(nameof(SecretKeepTitle));
    public static string SecretKeepCaption => Get(nameof(SecretKeepCaption));
    public static string SecretKeepNever => Get(nameof(SecretKeepNever));
    public static string SecretKeepSeconds => Get(nameof(SecretKeepSeconds));
    public static string SecretKeepMinutes => Get(nameof(SecretKeepMinutes));

    // ----- Maintenance: resource panel -----
    public static string ResourcesTitle => Get(nameof(ResourcesTitle));
    public static string ResourcesCaption => Get(nameof(ResourcesCaption));
    public static string ResourcesRefresh => Get(nameof(ResourcesRefresh));
    public static string DiagWorkingSet => Get(nameof(DiagWorkingSet));
    public static string DiagPrivateBytes => Get(nameof(DiagPrivateBytes));
    public static string DiagManagedHeap => Get(nameof(DiagManagedHeap));
    public static string DiagThreads => Get(nameof(DiagThreads));
    public static string DiagHooksActive => Get(nameof(DiagHooksActive));
    public static string DiagAssemblies => Get(nameof(DiagAssemblies));
    public static string DiagHeavyLoaded => Get(nameof(DiagHeavyLoaded));
    public static string DiagDbOnDisk => Get(nameof(DiagDbOnDisk));
    public static string DiagNone => Get(nameof(DiagNone));
    public static string DiagYes => Get(nameof(DiagYes));

    private static readonly Dictionary<string, string> PtSystem = new()
    {
        [nameof(SecretKeepTitle)] = "Manter segredos por",
        [nameof(SecretKeepCaption)] = "Senhas e tokens copiados ficam no histórico só por esse tempo, com a prévia mascarada, e depois são apagados. \"Não manter\" ignora a cópia.",
        [nameof(SecretKeepNever)] = "Não manter",
        [nameof(SecretKeepSeconds)] = "{0} s",
        [nameof(SecretKeepMinutes)] = "{0} min",
        [nameof(ResourcesTitle)] = "Recursos",
        [nameof(ResourcesCaption)] = "Quanto o Klip está usando agora: memória, threads, hooks, bibliotecas carregadas e o banco no disco.",
        [nameof(ResourcesRefresh)] = "Atualizar",
        [nameof(DiagWorkingSet)] = "Memória em uso (working set)",
        [nameof(DiagPrivateBytes)] = "Memória privada",
        [nameof(DiagManagedHeap)] = "Heap gerenciado",
        [nameof(DiagThreads)] = "Threads",
        [nameof(DiagHooksActive)] = "Hooks de teclado/mouse",
        [nameof(DiagAssemblies)] = "Bibliotecas carregadas",
        [nameof(DiagHeavyLoaded)] = "Componentes pesados carregados",
        [nameof(DiagDbOnDisk)] = "Banco no disco",
        [nameof(DiagNone)] = "nenhum",
        [nameof(DiagYes)] = "sim",
    };

    private static readonly Dictionary<string, string> EnSystem = new()
    {
        [nameof(SecretKeepTitle)] = "Keep secrets for",
        [nameof(SecretKeepCaption)] = "Copied passwords and tokens stay in the history only this long, with a masked preview, then get deleted. \"Don't keep\" skips the copy.",
        [nameof(SecretKeepNever)] = "Don't keep",
        [nameof(SecretKeepSeconds)] = "{0} s",
        [nameof(SecretKeepMinutes)] = "{0} min",
        [nameof(ResourcesTitle)] = "Resources",
        [nameof(ResourcesCaption)] = "What Klip is using right now: memory, threads, hooks, loaded libraries and the database on disk.",
        [nameof(ResourcesRefresh)] = "Refresh",
        [nameof(DiagWorkingSet)] = "Memory in use (working set)",
        [nameof(DiagPrivateBytes)] = "Private memory",
        [nameof(DiagManagedHeap)] = "Managed heap",
        [nameof(DiagThreads)] = "Threads",
        [nameof(DiagHooksActive)] = "Keyboard/mouse hooks",
        [nameof(DiagAssemblies)] = "Loaded libraries",
        [nameof(DiagHeavyLoaded)] = "Heavy components loaded",
        [nameof(DiagDbOnDisk)] = "Database on disk",
        [nameof(DiagNone)] = "none",
        [nameof(DiagYes)] = "yes",
    };
}
