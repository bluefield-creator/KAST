using KAST.Core.Enums;
using KAST.Core.Interfaces;
using KAST.Core.Models;
using KAST.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KAST.Infrastructure.Services;

public class ModPresetService(
    KastDbContext db,
    IModService modService,
    IServerInstanceService serverInstanceService,
    ILogger<ModPresetService> logger) : IModPresetService
{
    public async Task<IReadOnlyList<ModPreset>> GetPresetsForInstanceAsync(int instanceId, CancellationToken ct = default)
        => await db.ModPresets
            .AsNoTracking()
            .Where(p => p.ServerInstanceId == instanceId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task<ModPreset?> GetPresetByIdAsync(int id, CancellationToken ct = default)
        => await db.ModPresets
            .Include(p => p.Entries).ThenInclude(e => e.SteamMod)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<ModPreset> CreatePresetAsync(ModPreset preset, CancellationToken ct = default)
    {
        db.ModPresets.Add(preset);
        await db.SaveChangesAsync(ct);
        return preset;
    }

    public async Task<ModPreset> UpdatePresetAsync(ModPreset preset, CancellationToken ct = default)
    {
        var existing = await db.ModPresets.FindAsync([preset.Id], ct)
            ?? throw new InvalidOperationException($"Preset {preset.Id} not found");

        existing.Name = preset.Name;
        existing.ImagePath = preset.ImagePath;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeletePresetAsync(int id, CancellationToken ct = default)
    {
        var preset = await db.ModPresets
            .Include(p => p.Entries)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (preset == null) return;

        db.ModPresets.Remove(preset);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ModPreset> ImportFromArmaHtmlAsync(int instanceId, string name, string rawHtml, CancellationToken ct = default)
    {
        var preset = new ModPreset
        {
            ServerInstanceId = instanceId,
            Name = name,
            Type = ModPresetType.Arma,
            RawHtmlContent = rawHtml
        };

        db.ModPresets.Add(preset);
        await db.SaveChangesAsync(ct);

        // Parse the HTML to extract workshop IDs and names. A match timeout keeps
        // a hostile or malformed launcher export from pinning the request thread
        // in catastrophic backtracking.
        var matches = System.Text.RegularExpressions.Regex.Matches(rawHtml,
            @"<tr\s+data-type=""ModContainer"">(.*?)</tr>",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        foreach (var rowHtml in matches
                     .Cast<System.Text.RegularExpressions.Match>()
                     .Select(row => row.Groups[1].Value))
        {
            var idMatch = System.Text.RegularExpressions.Regex.Match(rowHtml,
                @"[?&]id=(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));

            if (!idMatch.Success || !long.TryParse(idMatch.Groups[1].Value, out var workshopId))
                continue;

            try
            {
                var mod = await modService.GetModByWorkshopIdAsync(workshopId, ct)
                    ?? await modService.AddWorkshopModAsync(workshopId, ct);

                preset.Entries.Add(new ModPresetEntry
                {
                    ModPresetId = preset.Id,
                    SteamModId = mod.Id,
                    IsClientSide = true,
                    IsServerSide = false,
                    LoadOrder = preset.Entries.Count
                });
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Failed to resolve mod {WorkshopId} for Arma preset import", workshopId);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to resolve mod {WorkshopId} for Arma preset import", workshopId);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Failed to resolve mod {WorkshopId} for Arma preset import", workshopId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to resolve mod {WorkshopId} for Arma preset import", workshopId);
            }
        }

        await db.SaveChangesAsync(ct);
        return preset;
    }

    public async Task ApplyPresetAsync(int presetId, int instanceId, CancellationToken ct = default)
    {
        var preset = await db.ModPresets
            .Include(p => p.Entries)
            .FirstOrDefaultAsync(p => p.Id == presetId, ct)
            ?? throw new InvalidOperationException($"Preset {presetId} not found");

        // Diff against the current assignments instead of remove-all + re-add:
        // deleting and inserting the same (ServerInstanceId, SteamModId) composite
        // key in one SaveChanges is an EF statement-ordering hazard.
        var existingMods = await db.ServerInstanceMods
            .Where(m => m.ServerInstanceId == instanceId)
            .ToDictionaryAsync(m => m.SteamModId, ct);

        var applied = new HashSet<int>();
        foreach (var entry in preset.Entries.OrderBy(e => e.LoadOrder))
        {
            // Verify the mod still exists
            var modExists = await db.Mods.AnyAsync(m => m.Id == entry.SteamModId, ct);
            if (!modExists)
            {
                logger.LogWarning("Preset {PresetId} references mod {ModId} which no longer exists — skipping", presetId, entry.SteamModId);
                continue;
            }

            applied.Add(entry.SteamModId);
            if (existingMods.TryGetValue(entry.SteamModId, out var current))
            {
                current.IsClientSide = entry.IsClientSide;
                current.IsServerSide = entry.IsServerSide;
                current.LoadOrder = entry.LoadOrder;
            }
            else
            {
                db.ServerInstanceMods.Add(new ServerInstanceMod
                {
                    ServerInstanceId = instanceId,
                    SteamModId = entry.SteamModId,
                    IsClientSide = entry.IsClientSide,
                    IsServerSide = entry.IsServerSide,
                    LoadOrder = entry.LoadOrder
                });
            }
        }

        db.ServerInstanceMods.RemoveRange(existingMods.Values.Where(m => !applied.Contains(m.SteamModId)));

        preset.LastAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await serverInstanceService.LinkModsAsync(instanceId, ct);
    }

    public async Task<ModPreset> SaveInstanceAsKastPresetAsync(int instanceId, string name, CancellationToken ct = default)
    {
        var instanceMods = await db.ServerInstanceMods
            .Where(m => m.ServerInstanceId == instanceId)
            .OrderBy(m => m.LoadOrder)
            .ToListAsync(ct);

        var preset = new ModPreset
        {
            ServerInstanceId = instanceId,
            Name = name,
            Type = ModPresetType.Kast
        };

        db.ModPresets.Add(preset);
        await db.SaveChangesAsync(ct);

        foreach (var im in instanceMods)
        {
            preset.Entries.Add(new ModPresetEntry
            {
                ModPresetId = preset.Id,
                SteamModId = im.SteamModId,
                IsClientSide = im.IsClientSide,
                IsServerSide = im.IsServerSide,
                LoadOrder = im.LoadOrder
            });
        }

        await db.SaveChangesAsync(ct);
        return preset;
    }
}
