namespace NexusKit.Modules.InternalData.History;

/// <summary>
/// Discriminator for entries persisted to <c>nexus_internal_player_history</c>.
/// Stored as a byte so adding more kinds later doesn't break existing rows.
/// </summary>
public enum PlayerHistoryKind : byte
{
    /// <summary>Character was renamed.</summary>
    NameChange = 1,

    /// <summary>Character was transferred to a different home world.</summary>
    HomeWorldChange = 2,

    /// <summary>Race or gender of the character changed (Fantasia or similar).
    /// Hair/face/scar tweaks are intentionally not tracked — they're too noisy.</summary>
    CustomizeChange = 3,

    /// <summary>FC identity changed — the <c>free_company_lodestone_id</c>
    /// on the Lodestone profile differs from the last stored value. Sole FC
    /// signal: tag-based detection was retired because FC tags can collide
    /// across worlds (multiple FCs with the same tag on the same world), so
    /// the live-object-table tag was never a reliable change signal.
    /// OldValue / NewValue carry FC Lodestone ids — UI resolves them to FC
    /// names on render.
    /// <para>Enum value <c>4</c> is retained from the prior
    /// <c>FreeCompanyTagChange</c> name so existing history rows keep their
    /// semantics under the renamed kind without a data migration. Legacy
    /// tag-string OldValue/NewValue payloads on those rows render through
    /// the same UI path; new rows carry FC Lodestone ids.</para></summary>
    FreeCompanyChange = 4,

    /// <summary>The character's in-game search comment (Search Info) changed.
    /// OldValue / NewValue carry the raw text; either may be null, which is how
    /// "set for the first time" and "cleared" are expressed.
    /// <para>Unlike every kind above, this one is not produced by the
    /// observation diff — the search comment is not in the object table. It is
    /// written by the Examine capture path, so a row only ever appears for a
    /// character the user examined at least twice with a different comment in
    /// between (or once, if they had none on file before).</para>
    /// <para>Not the Lodestone biography — that is a different datum and is not
    /// tracked here.</para></summary>
    SearchCommentChange = 5,
}
