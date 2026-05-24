using Microsoft.EntityFrameworkCore;
using NexusKit.Persistence.Schema;

namespace NexusKit.Modules.ExternalData.Persistence;

/// <summary>
/// Persists only what's tied to a specific player: identity, profile, per-collection
/// stats, owned-entry IDs, gear, class-jobs. Catalog details (mount/minion/item/etc.
/// names + icons) are resolved on demand via NexusKit.GameData / source modules.
/// </summary>
internal sealed class ExternalDataEntityModule : IEntityModule
{
    public string TablePrefix => "external";

    public void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlayerEntity>(e =>
        {
            e.ToTable("player");
            e.HasKey(x => x.LodestoneId);
            e.Property(x => x.LodestoneId).HasColumnName("lodestone_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            e.Property(x => x.HomeWorldId).HasColumnName("home_world_id");
            e.Property(x => x.DataCenterId).HasColumnName("data_center_id");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            // GetByNameAsync — the watcher's per-observation cache lookup — filters
            // on (name, home_world_id). PK is on lodestone_id so this hot path
            // otherwise full-scans the player table.
            e.HasIndex(x => new { x.Name, x.HomeWorldId });
        });

        modelBuilder.Entity<PlayerProfileEntity>(e =>
        {
            e.ToTable("player_profile");
            e.HasKey(x => x.LodestoneId);
            e.Property(x => x.LodestoneId).HasColumnName("lodestone_id");
            e.Property(x => x.Bio).HasColumnName("bio");
            e.Property(x => x.Nameday).HasColumnName("nameday").HasMaxLength(64);
            e.Property(x => x.GuardianDeity).HasColumnName("guardian_deity").HasMaxLength(64);
            e.Property(x => x.GuardianDeityIconUrl).HasColumnName("guardian_deity_icon_url");
            e.Property(x => x.StartingCity).HasColumnName("starting_city").HasMaxLength(64);
            e.Property(x => x.StartingCityIconUrl).HasColumnName("starting_city_icon_url");
            e.Property(x => x.AvatarUrl).HasColumnName("avatar_url");
            e.Property(x => x.PortraitUrl).HasColumnName("portrait_url");
            e.Property(x => x.FreeCompanyLodestoneId).HasColumnName("free_company_lodestone_id").HasMaxLength(32);
            e.Property(x => x.PvpTeamLodestoneId).HasColumnName("pvp_team_lodestone_id").HasMaxLength(32);
            e.Property(x => x.PvpTeamName).HasColumnName("pvp_team_name").HasMaxLength(128);
            e.Property(x => x.GrandCompanyId).HasColumnName("grand_company_id");
            e.Property(x => x.GrandCompanyRankId).HasColumnName("grand_company_rank_id");
            e.Property(x => x.GrandCompanyRankIsFeminine).HasColumnName("grand_company_rank_is_feminine");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<PlayerCollectionStatsEntity>(e =>
        {
            e.ToTable("player_collection_stats");
            e.HasKey(x => new { x.LodestoneId, x.Kind });
            e.Property(x => x.LodestoneId).HasColumnName("lodestone_id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Count).HasColumnName("count");
            e.Property(x => x.Total).HasColumnName("total");
            e.Property(x => x.Ranking).HasColumnName("ranking");
            e.Property(x => x.IsPublic).HasColumnName("is_public");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<PlayerOwnedEntryEntity>(e =>
        {
            e.ToTable("player_owned");
            e.HasKey(x => new { x.LodestoneId, x.Kind, x.EntryId });
            e.Property(x => x.LodestoneId).HasColumnName("lodestone_id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.EntryId).HasColumnName("entry_id");
            e.Property(x => x.AchievedAt).HasColumnName("achieved_at");
        });

        modelBuilder.Entity<PlayerClassJobEntity>(e =>
        {
            e.ToTable("player_class_job");
            e.HasKey(x => new { x.LodestoneId, x.ClassJobId });
            e.Property(x => x.LodestoneId).HasColumnName("lodestone_id");
            e.Property(x => x.ClassJobId).HasColumnName("class_job_id");
            e.Property(x => x.Level).HasColumnName("level");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<PlayerGearSlotEntity>(e =>
        {
            e.ToTable("player_gear_slot");
            e.HasKey(x => new { x.LodestoneId, x.SlotIndex });
            e.Property(x => x.LodestoneId).HasColumnName("lodestone_id");
            e.Property(x => x.SlotIndex).HasColumnName("slot_index");
            e.Property(x => x.ItemId).HasColumnName("item_id");
            e.Property(x => x.IsHq).HasColumnName("is_hq");
            e.Property(x => x.GlamourItemId).HasColumnName("glamour_item_id");
            e.Property(x => x.ColorsJson).HasColumnName("colors_json");
            e.Property(x => x.MateriaJson).HasColumnName("materia_json");
            e.Property(x => x.CreatorName).HasColumnName("creator_name").HasMaxLength(64);
            e.Property(x => x.ItemLevel).HasColumnName("item_level");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<FreeCompanyEntity>(e =>
        {
            e.ToTable("free_company");
            e.HasKey(x => x.LodestoneId);
            e.Property(x => x.LodestoneId).HasColumnName("lodestone_id").HasMaxLength(32);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            e.Property(x => x.Tag).HasColumnName("tag").HasMaxLength(16);
            e.Property(x => x.Slogan).HasColumnName("slogan");
            e.Property(x => x.FormedAt).HasColumnName("formed_at");
            e.Property(x => x.WorldId).HasColumnName("world_id");
            e.Property(x => x.Rank).HasColumnName("rank");
            e.Property(x => x.ActiveMemberCount).HasColumnName("active_member_count");
            e.Property(x => x.ActiveState).HasColumnName("active_state_id");
            e.Property(x => x.RecruitmentOpen).HasColumnName("recruitment_open");
            e.Property(x => x.GrandCompanyId).HasColumnName("grand_company_id");
            e.Property(x => x.EstateAddressId).HasColumnName("estate_address_id");
            e.Property(x => x.FocusRolePlay).HasColumnName("focus_role_play");
            e.Property(x => x.FocusLeveling).HasColumnName("focus_leveling");
            e.Property(x => x.FocusCasual).HasColumnName("focus_casual");
            e.Property(x => x.FocusHardcore).HasColumnName("focus_hardcore");
            e.Property(x => x.FocusDungeons).HasColumnName("focus_dungeons");
            e.Property(x => x.FocusGuildhests).HasColumnName("focus_guildhests");
            e.Property(x => x.FocusTrials).HasColumnName("focus_trials");
            e.Property(x => x.FocusRaids).HasColumnName("focus_raids");
            e.Property(x => x.FocusPvP).HasColumnName("focus_pvp");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            // (tag, world_id) is the lookup key for the candidate-list fallback
            // used by FreeCompanyTab when no profile-linked Lodestone FC id is
            // available. Non-unique — the same (tag, world_id) can legitimately
            // belong to multiple FCs (e.g. «Boop!» on Twintania).
            e.HasIndex(x => new { x.Tag, x.WorldId });

            // FK relationship to EstateAddressEntity. WithMany() (not WithOne())
            // because the address table is conceptually shared — future player
            // entities will also reference it. The 1:1 "at most one address per
            // FC" is enforced by the EstateAddressId column being a single
            // scalar nullable FK; collisions across owners are allowed.
            e.HasOne<EstateAddressEntity>()
                .WithMany()
                .HasForeignKey(x => x.EstateAddressId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.EstateAddressId)
                .HasDatabaseName("ix_nexus_external_free_company_estate_address");
        });
    }
}