using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The collections a declared DDMS serves (osdu/docs/reference/flow/ddms-services.md): the ones it lists, or its
// shape's own when it lists none, each checked as the DDMS of its shape serves it.
internal static partial class FlowMapper
{
    /// <summary>The collections a declared DDMS serves, or those of its shape when it lists none.</summary>
    private static IReadOnlyList<DdmsCollectionEntry> MapDdmsCollections(
        OrderedDictionary<string, DdmsCollectionYaml?>? declared, DdmsShape shape, string at, string source)
    {
        if (declared is null)
        {
            return DdmsCatalog.DefaultCollections(shape);
        }

        if (shape == DdmsShape.ProductionTimeSeriesV1)
        {
            throw new FlowValidationException(
                $"{source}: {at}.collections lists collections, and the historian serves {DdmsCatalog.ProductionValues} records alone, under production-values. Leave collections out.");
        }

        if (declared.Count == 0)
        {
            throw new FlowValidationException(
                $"{source}: {at}.collections lists no collection. List the entity types the DDMS serves, or leave collections out for the ones its shape serves.");
        }

        var collections = new List<DdmsCollectionEntry>(declared.Count);
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in declared)
        {
            var entityType = key?.Trim() ?? string.Empty;
            if (!DdmsCatalog.IsEntityType(entityType))
            {
                throw new FlowValidationException(
                    $"{source}: {at}.collections names '{entityType}', which is not an OSDU entity type with its group, such as work-product-component--WellLog.");
            }

            if (!types.Add(entityType))
            {
                throw new FlowValidationException($"{source}: {at}.collections lists {entityType} more than once (entity types are compared ignoring case).");
            }

            var entry = $"{at}.collections.{entityType}";
            if (shape == DdmsShape.WellDeliveryV1)
            {
                collections.Add(MapWellDeliveryCollection(entityType, value, entry, source));
                continue;
            }

            if (shape == DdmsShape.SeismicStoreV3)
            {
                collections.Add(MapSeismicCollection(entityType, value, entry, source));
                continue;
            }

            if (shape == DdmsShape.ReservoirManagement)
            {
                collections.Add(MapReservoirManagementCollection(entityType, value, entry, source));
                continue;
            }

            var declaredCollection = value ?? throw new FlowValidationException($"{source}: {entry} declares nothing; name at least the path the DDMS serves it under.");
            var segment = declaredCollection.Path?.Trim() ?? string.Empty;
            if (!DdmsCatalog.IsSegment(segment))
            {
                throw new FlowValidationException(
                    $"{source}: {entry}.path '{segment}' must be the collection's path segment, such as welllogs: letters, digits, '.', '_' and '-', at most 100 characters.");
            }

            var bulk = declaredCollection.Bulk ?? false;
            var columns = ParseEnum(declaredCollection.Columns, DdmsBulkColumns.Unchecked, entry + ".columns", source);
            if (!bulk && columns != DdmsBulkColumns.Unchecked)
            {
                throw new FlowValidationException($"{source}: {entry}.columns says what bulk data columns are checked against, and the collection holds records alone (bulk is false).");
            }

            if (shape != DdmsShape.WellboreDdmsV3 && columns != DdmsBulkColumns.Unchecked)
            {
                throw new FlowValidationException(
                    $"{source}: {entry}.columns names the curve and station checks the Wellbore DDMS applies to its bulk data, and {at} has the {DdmsCatalog.ShapeName(shape)} shape.");
            }

            var typed = declaredCollection.TypedContent ?? false;
            if (typed && (shape != DdmsShape.RafsV2 || !bulk))
            {
                throw new FlowValidationException(
                    shape != DdmsShape.RafsV2
                        ? $"{source}: {entry}.typedContent says a RAFS collection holds several content types, and {at} has the {DdmsCatalog.ShapeName(shape)} shape."
                        : $"{source}: {entry}.typedContent says what content the collection holds, and it holds records alone (bulk is false).");
            }

            collections.Add(new DdmsCollectionEntry(entityType, segment, bulk) { Columns = columns, TypedContent = typed });
        }

        return collections;
    }

    /// <summary>
    /// A collection of a declared Well Delivery DDMS: always the entity type lowercased, which is the path the service
    /// takes the type from, and records alone. A declared path must be that segment.
    /// </summary>
    private static DdmsCollectionEntry MapWellDeliveryCollection(string entityType, DdmsCollectionYaml? declared, string entry, string source)
    {
        var collection = DdmsCatalog.WellDeliveryCollection(entityType);
        if (declared is null)
        {
            return collection;
        }

        if (declared.Path is { } path && !string.Equals(path.Trim(), collection.Segment, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: {entry}.path '{path.Trim()}' is not the path the Well Delivery DDMS serves {entityType} under: it takes the type from the record id and requires the path to be that type, "
                + $"so the path is {collection.Segment}. Leave path out.");
        }

        if (declared.Bulk == true || declared.Columns is not null || declared.TypedContent is not null)
        {
            throw new FlowValidationException($"{source}: {entry} describes bulk data, and the Well Delivery DDMS keeps records alone. Remove bulk, columns and typedContent.");
        }

        return collection;
    }

    /// <summary>
    /// A dataset type a declared Seismic Store registers datasets for: a <c>dataset--FileCollection.*</c> type, which always
    /// keeps files. Its path only names it (the type after <c>dataset--FileCollection.</c>, lowercased, when left out),
    /// since every dataset goes to the subproject whatever its type.
    /// </summary>
    private static DdmsCollectionEntry MapSeismicCollection(string entityType, DdmsCollectionYaml? declared, string entry, string source)
    {
        if (!entityType.StartsWith(DdmsCatalog.FileCollectionPrefix, StringComparison.OrdinalIgnoreCase) || entityType.Length == DdmsCatalog.FileCollectionPrefix.Length)
        {
            throw new FlowValidationException(
                $"{source}: {entry} names a type Seismic Store registers no dataset for; a Seismic Store dataset's record is a {DdmsCatalog.FileCollectionPrefix}* record.");
        }

        var segment = declared?.Path?.Trim() is { Length: > 0 } path ? path : entityType[DdmsCatalog.FileCollectionPrefix.Length..].ToLowerInvariant();
        if (!DdmsCatalog.IsSegment(segment))
        {
            throw new FlowValidationException($"{source}: {entry}.path '{segment}' must be a name of letters, digits, '.', '_' and '-', at most 100 characters.");
        }

        if (declared?.Bulk == false || declared?.Columns is not null || declared?.TypedContent is not null)
        {
            throw new FlowValidationException($"{source}: {entry} says what a dataset keeps, and a Seismic Store dataset always keeps its files; remove bulk, columns and typedContent.");
        }

        return new DdmsCollectionEntry(entityType, segment, Bulk: true);
    }

    /// <summary>
    /// An entity type a declared Reservoir Management DDMS serves: under one of its nine header collections, the one whose
    /// records the service keeps being named by <c>path</c> (the collection the service's own entity type has when left
    /// out). The collection's tables decide whether its records keep rows, so bulk, columns and typedContent are not given.
    /// </summary>
    private static DdmsCollectionEntry MapReservoirManagementCollection(string entityType, DdmsCollectionYaml? declared, string entry, string source)
    {
        var known = DdmsCatalog.ReservoirManagementCollections.FirstOrDefault(c => string.Equals(c.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
        var written = declared?.Path?.Trim();
        var segment = written is { Length: > 0 } ? written : known?.Segment;
        var segments = string.Join(", ", ReservoirManagementTables.Headers.Select(h => h.Segment));
        if (segment is null)
        {
            throw new FlowValidationException(
                $"{source}: {entry}.path is required: the header collection of the Reservoir Management DDMS the records go to ({segments}); {entityType} is none of its own entity types.");
        }

        var header = ReservoirManagementTables.Header(segment)
            ?? throw new FlowValidationException($"{source}: {entry}.path '{segment}' is not a header collection of the Reservoir Management DDMS: {segments}.");
        if (declared?.Bulk is not null || declared?.Columns is not null || declared?.TypedContent is not null)
        {
            throw new FlowValidationException(
                $"{source}: {entry} says what the collection keeps, and the Reservoir Management DDMS's tables decide that ({header.Segment} keeps "
                + (header.Tables.Count == 0 ? "no rows" : "rows in " + string.Join(", ", header.Tables)) + "); remove bulk, columns and typedContent.");
        }

        return new DdmsCollectionEntry(entityType, header.Segment, Bulk: header.Tables.Count > 0);
    }
}
