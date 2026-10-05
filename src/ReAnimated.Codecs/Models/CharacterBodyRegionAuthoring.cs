using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record ReviewedBodyRegionAssembly(string ResourceId, int BodyElementCallIndex, string ExpectedSourceSha256,
    ImmutableArray<string> CutCapSurfaceIds, ImmutableArray<string> DetachedResourceIds,
    ImmutableArray<string> PhysicsResourceIds, ImmutableArray<string> EffectResourceIds,
    bool ArtistGeometryReviewed, bool RelationshipsReviewed);

public static class CharacterBodyRegionAuthoring
{
    public static FbxModelAuthoringImportResult Apply(FbxModelAuthoringImportResult model, ReviewedBodyRegionAssembly proposal)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(proposal);
        if (!proposal.ArtistGeometryReviewed || !proposal.RelationshipsReviewed)
            throw new InvalidOperationException("Review the artist-made geometry and each body-region relationship before recording the assembly.");
        var inventory = model.Package.Document.CharacterResources ?? throw new InvalidDataException("Attach original character systems before reviewing a body region.");
        var read = CharacterCompanionAuthoring.Read(model.Package, proposal.ResourceId, CharacterCompanionFamily.BodyElements);
        if (!string.Equals(read.Resource.ContentSha256, proposal.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase) || !read.IsValid)
            throw new InvalidDataException("The selected body-element source changed or cannot be parsed.");
        var element = read.BodyElements!.Elements.SingleOrDefault(element => element.CallIndex == proposal.BodyElementCallIndex)
            ?? throw new InvalidDataException("The selected body region no longer exists.");
        var bones = model.Package.Document.CreateEffectiveBones().Where(bone => bone.Name == element.HelperName).ToArray();
        if (bones.Length != 1) throw new InvalidDataException("The body-region helper is missing or ambiguous.");
        var surfaces = model.Surfaces.ToDictionary(surface => surface.Id, StringComparer.Ordinal);
        if (proposal.CutCapSurfaceIds.IsDefaultOrEmpty || proposal.CutCapSurfaceIds.Distinct(StringComparer.Ordinal).Count() != proposal.CutCapSurfaceIds.Length ||
            proposal.CutCapSurfaceIds.Any(id => !surfaces.ContainsKey(id)))
            throw new InvalidDataException("Select existing artist-made cut/cap surfaces for this region.");
        var original = DecodedCharacterSnapshotCodec.DecodeSource(model.Package).Surfaces.ToDictionary(surface => surface.Id, StringComparer.Ordinal);
        foreach (string capId in proposal.CutCapSurfaceIds)
            if (original.TryGetValue(capId, out var baseline) && baseline.MeshName == surfaces[capId].MeshName &&
                baseline.Indices.SequenceEqual(surfaces[capId].Indices) && baseline.Vertices.Select(vertex=>vertex.Position).SequenceEqual(surfaces[capId].Vertices.Select(vertex=>vertex.Position)))
                throw new InvalidDataException("Select artist-supplied cap geometry, not an unchanged original body surface.");
        var disables = read.BodyElements.MeshDisables.Where(disable => disable.BodyElementCallIndex == element.CallIndex).ToArray();
        foreach (var disable in disables)
            _ = CharacterModelEntityInventory.RequireUnique(model, disable.EntityName);
        var bodyHidden = disables.Where(disable => !disable.FromRelic).Select(disable => disable.EntityName).ToHashSet(StringComparer.Ordinal);
        if (proposal.CutCapSurfaceIds.Any(id => bodyHidden.Contains(surfaces[id].MeshName)))
            throw new InvalidDataException("A selected cap is hidden by the region's original-body disable list.");
        var relics = read.BodyElements.Relics.Where(relic => relic.BodyElementCallIndex == element.CallIndex).ToArray();
        var detached = SelectAssets(model.Package, proposal.DetachedResourceIds, [".msh", ".skn"]);
        if (detached.Any(asset=>asset.ResourceId==inventory.RootResourceId))
            throw new InvalidDataException("The original character root cannot be selected as detached-part geometry.");
        var physics = SelectAssets(model.Package, proposal.PhysicsResourceIds, [".phx"]);
        var effects = SelectAssets(model.Package, proposal.EffectResourceIds, [".fx"]);
        if (relics.Length > 0 && detached.IsEmpty)
            throw new InvalidDataException("Select retained detached geometry for the declared relics. This selection does not resolve a conditional native filename branch.");
        foreach (var name in relics.Select(relic => relic.PhysicsResource).Distinct(StringComparer.Ordinal)) RequireExactDeclared(inventory, name, physics);
        foreach (var name in relics.Select(relic => relic.EffectResource).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal)) RequireExactDeclared(inventory, name, effects);
        var receipt = new CharacterBodyRegionAssemblyReview
        {
            BodyResourceId = proposal.ResourceId, BodySourceSha256 = read.Resource.ContentSha256!, BodyElementCallIndex = element.CallIndex,
            ElementToken = element.ElementToken, HelperName = element.HelperName, HelperFrameSha256 = HelperFingerprint(model, element.HelperName),
            GeometrySha256 = GeometryFingerprint(model), CutCapSurfaceIds = proposal.CutCapSurfaceIds,
            BodyHideEntityNames = disables.Where(disable => !disable.FromRelic).Select(disable => disable.EntityName).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            RelicHideEntityNames = disables.Where(disable => disable.FromRelic).Select(disable => disable.EntityName).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            DetachedAssets = detached, PhysicsAssets = physics, EffectAssets = effects,
            ArtistGeometryReviewed = true, RelationshipsReviewed = true, Accepted = true,
        };
        receipt.Validate();
        var updated = inventory with
        {
            BodyRegionReviews = inventory.BodyRegionReviews.Where(review => review.BodyResourceId != receipt.BodyResourceId || review.BodyElementCallIndex != receipt.BodyElementCallIndex).Append(receipt).ToImmutableArray(),
            CompiledSemanticSha256 = null, LoadedResourceSha256 = null, VerifiedPlayerScenarios = [],
        };
        return ModelGeometryRevisionCodec.Capture(model with { Package = model.Package with { Document = model.Package.Document with { CharacterResources = updated, LastBuildReceipt = null } } });
    }

    public static FbxModelAuthoringImportResult Reconcile(FbxModelAuthoringImportResult model)
    {
        var inventory = model.Package.Document.CharacterResources;
        if (inventory is null || inventory.BodyRegionReviews.IsEmpty) return model;
        string geometry = GeometryFingerprint(model);
        var reviews = inventory.BodyRegionReviews.Select(review => review.Accepted && !Matches(model, review, geometry) ? review with { Accepted = false } : review).ToImmutableArray();
        if (reviews.SequenceEqual(inventory.BodyRegionReviews)) return model;
        return model with { Package = model.Package with { Document = model.Package.Document with { LastBuildReceipt = null,
            CharacterResources = inventory with { BodyRegionReviews = reviews, CompiledSemanticSha256 = null, LoadedResourceSha256 = null, VerifiedPlayerScenarios = [] } } } };
    }

    public static ImmutableArray<string> ExportBlockers(FbxModelAuthoringImportResult model)
    {
        var reviews = model.Package.Document.CharacterResources?.BodyRegionReviews ?? [];
        if (reviews.IsEmpty) return [];
        string geometry = GeometryFingerprint(model);
        return reviews.Where(review => !review.Accepted || !Matches(model, review, geometry))
            .Select(review => $"Body region '{review.ElementToken}' has stale authoring geometry, helper, source or asset selections.").ToImmutableArray();
    }
    private static bool Matches(FbxModelAuthoringImportResult model, CharacterBodyRegionAssemblyReview review, string geometry)
    {
        try
        {
            var read = CharacterCompanionAuthoring.Read(model.Package, review.BodyResourceId, CharacterCompanionFamily.BodyElements);
            var element = read.BodyElements!.Elements.SingleOrDefault(element => element.CallIndex == review.BodyElementCallIndex);
            if (!read.IsValid || read.Resource.ContentSha256 != review.BodySourceSha256 || element is null ||
                element.ElementToken != review.ElementToken || element.HelperName != review.HelperName || geometry != review.GeometrySha256 ||
                HelperFingerprint(model, review.HelperName) != review.HelperFrameSha256 || review.CutCapSurfaceIds.Any(id => !model.Surfaces.Any(surface => surface.Id == id))) return false;
            var original = DecodedCharacterSnapshotCodec.DecodeSource(model.Package).Surfaces.ToDictionary(surface => surface.Id, StringComparer.Ordinal);
            foreach (string capId in review.CutCapSurfaceIds)
            {
                var cap = model.Surfaces.Single(surface => surface.Id == capId);
                if (original.TryGetValue(capId, out var baseline) && baseline.MeshName == cap.MeshName &&
                    baseline.Indices.SequenceEqual(cap.Indices) && baseline.Vertices.Select(vertex=>vertex.Position).SequenceEqual(cap.Vertices.Select(vertex=>vertex.Position))) return false;
            }
            var disables = read.BodyElements.MeshDisables.Where(disable => disable.BodyElementCallIndex == element.CallIndex).ToArray();
            foreach (var disable in disables) _ = CharacterModelEntityInventory.RequireUnique(model, disable.EntityName);
            if (!review.BodyHideEntityNames.SequenceEqual(disables.Where(disable => !disable.FromRelic).Select(disable => disable.EntityName).Distinct(StringComparer.Ordinal)) ||
                !review.RelicHideEntityNames.SequenceEqual(disables.Where(disable => disable.FromRelic).Select(disable => disable.EntityName).Distinct(StringComparer.Ordinal))) return false;
            var relics = read.BodyElements.Relics.Where(relic => relic.BodyElementCallIndex == element.CallIndex).ToArray();
            if (relics.Length > 0 && review.DetachedAssets.IsEmpty) return false;
            if (review.DetachedAssets.Any(asset=>asset.ResourceId==model.Package.Document.CharacterResources!.RootResourceId)) return false;
            var inventory = model.Package.Document.CharacterResources!;
            foreach (string physics in relics.Select(relic => relic.PhysicsResource).Distinct(StringComparer.Ordinal)) RequireExactDeclared(inventory, physics, review.PhysicsAssets);
            foreach (string effect in relics.Select(relic => relic.EffectResource).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal)) RequireExactDeclared(inventory, effect, review.EffectAssets);
            var resources = model.Package.Document.CharacterResources!.Resources;
            return review.DetachedAssets.Concat(review.PhysicsAssets).Concat(review.EffectAssets).All(asset => resources.Any(resource =>
                !resource.IsOriginalArchive && resource.EntryPath is not null && resource.Id == asset.ResourceId && resource.LogicalName == asset.LogicalName && resource.ContentSha256 == asset.ContentSha256 && model.Package.CompanionPayloads.TryGetValue(resource.EntryPath!, out var payload) &&
                payload.Length == resource.ByteLength && string.Equals(Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())), asset.ContentSha256, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException) { return false; }
    }
    private static ImmutableArray<CharacterGoreAssetSelection> SelectAssets(CustomModelPackage package, ImmutableArray<string> ids, string[] extensions)
    {
        if (ids.IsDefault || ids.Length > 256 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("Select bounded, unique retained resources for this body region.");
        var inventory = package.Document.CharacterResources!;
        return ids.Select(id =>
        {
            var resource = inventory.Resources.SingleOrDefault(resource => resource.Id == id && !resource.IsOriginalArchive && resource.EntryPath is not null)
                ?? throw new InvalidDataException("Selected gore resource is missing: " + id);
            if (!package.CompanionPayloads.TryGetValue(resource.EntryPath!, out var payload) || payload.Length != resource.ByteLength ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())), resource.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected gore resource payload changed: " + resource.LogicalName);
            if (!extensions.Contains(System.IO.Path.GetExtension(resource.LogicalName), StringComparer.OrdinalIgnoreCase) &&
                !(extensions.Contains(".msh",StringComparer.OrdinalIgnoreCase) && resource.NativeResource is {ResourceType:272}))
                throw new InvalidDataException("Selected gore resource has the wrong type: " + resource.LogicalName);
            return new CharacterGoreAssetSelection(resource.Id, resource.LogicalName, resource.ContentSha256!);
        }).ToImmutableArray();
    }
    private static void RequireExactDeclared(CharacterResourceInventory inventory, string name, ImmutableArray<CharacterGoreAssetSelection> selected)
    {
        if (inventory.Resources.Count(resource => !resource.IsOriginalArchive && resource.EntryPath is not null && (resource.LogicalName == name || System.IO.Path.GetFileName(resource.LogicalName) == name)) != 1 ||
            selected.Count(asset => asset.LogicalName == name || System.IO.Path.GetFileName(asset.LogicalName) == name) != 1)
            throw new InvalidDataException("The declared physics/effect reference must resolve to one selected retained resource: " + name);
    }
    private static string GeometryFingerprint(FbxModelAuthoringImportResult model)
    {
        var surfaces = model.Surfaces.OrderBy(surface=>surface.Id,StringComparer.Ordinal).Select(surface=>new
        {
            surface.Id, surface.MeshName, surface.MaterialId, surface.IsSkinned, surface.PaletteBoneIndices, surface.InverseBindMatrices,
            Vertices = surface.Vertices.Select(vertex=>new {vertex.Position,vertex.Normal,vertex.BoneIndices,vertex.BoneWeights}),
            surface.Indices,
        });
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(surfaces);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    private static string HelperFingerprint(FbxModelAuthoringImportResult model, string name)
    {
        var bones = model.Package.Document.CreateEffectiveBones();
        var bone = bones.Single(bone => bone.Name == name);
        var globals = new ReAnimated.Core.Mathematics.TransformMatrix[bones.Length];
        foreach (var current in bones)
            globals[current.Index] = current.ParentIndex < 0 ? current.ExactLocalBindMatrix : globals[current.ParentIndex] * current.ExactLocalBindMatrix;
        string text = System.Text.Json.JsonSerializer.Serialize(new { bone.Name, bone.ParentIndex, bone.ExactLocalBindMatrix, World = globals[bone.Index] });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
