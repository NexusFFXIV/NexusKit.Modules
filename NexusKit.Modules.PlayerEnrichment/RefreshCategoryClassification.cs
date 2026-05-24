using NexusKit.Modules.InternalData.Refresh;

namespace NexusKit.Modules.PlayerEnrichment;

/// <summary>
/// Single source of truth for the structural facts about
/// <see cref="RefreshCategory"/> values that the refresh-queue layer cares
/// about: which categories are mandatory (always-on), which are user-toggleable,
/// and which qualify as "sub-resource" categories for the
/// <c>EnqueueAllAsync</c> / stale-sweep paths.
/// <para>Everything else — the policy implementation, the queue service's
/// pick filter, the disabled-category prune contributor — derives its sets
/// from these properties so the rules stay consistent when categories are
/// added or re-classified.</para>
/// </summary>
public static class RefreshCategoryClassification
{
    /// <summary>The complete enumeration, materialised once for callers that
    /// need to walk every category (e.g. <see cref="Mandatory"/> /
    /// <see cref="Toggleable"/> derivation, allowed-set construction in the
    /// worker pick filter).</summary>
    public static IReadOnlyList<RefreshCategory> All { get; }
        = Enum.GetValues<RefreshCategory>();

    /// <summary>True for categories the user must never be able to turn off.
    /// <c>LodestoneId</c> is the prerequisite for every sub-resource fetch;
    /// <c>FreeCompany</c> drives the strong-match <c>FreeCompanyChange</c>
    /// history pipeline and the FC tab's primary data source.</summary>
    public static bool IsMandatory(RefreshCategory category) => category switch
    {
        RefreshCategory.LodestoneId => true,
        RefreshCategory.FreeCompany => true,
        _ => false,
    };

    /// <summary>The mandatory subset of <see cref="All"/> — these categories
    /// always report <c>IsEnabled == true</c> regardless of stored settings,
    /// and the disabled-category prune leaves them alone.</summary>
    public static IReadOnlyList<RefreshCategory> Mandatory { get; }
        = All.Where(IsMandatory).ToArray();

    /// <summary>The user-toggleable subset of <see cref="All"/> — categories
    /// the settings UI renders checkboxes for, and the settings-backed
    /// policy actually consults its POCO for. Equals
    /// <c>All \ Mandatory \ {LodestoneId}</c>; LodestoneId is mandatory so
    /// the subtraction collapses to <c>All \ Mandatory</c>.</summary>
    public static IReadOnlyList<RefreshCategory> Toggleable { get; }
        = All.Where(c => !IsMandatory(c)).ToArray();

    /// <summary>Every category except <see cref="RefreshCategory.LodestoneId"/>
    /// — i.e. the categories the worker fetches data for after the id is
    /// resolved. Used by <c>EnqueueAllAsync</c> when the user clicks "Refresh
    /// from Lodestone" and by the stale-sweep cascade after a fresh id
    /// resolution. Includes mandatory <c>FreeCompany</c>.</summary>
    public static IReadOnlyList<RefreshCategory> SubResources { get; }
        = All.Where(c => c != RefreshCategory.LodestoneId).ToArray();
}
