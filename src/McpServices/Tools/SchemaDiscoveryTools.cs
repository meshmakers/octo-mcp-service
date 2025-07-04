using System.ComponentModel;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.Services.Infrastructure;
using ModelContextProtocol.Server;

namespace Meshmakers.Octo.Backend.McpServices.Tools;

/// <summary>
/// Tools for discovering and exploring Construction Kit schemas
/// </summary>
[McpServerToolType]
public sealed class SchemaDiscoveryTools
{
    /// <summary>
    /// Get all available Construction Kit types in the system
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="includeAbstract">Include abstract types in results</param>
    /// <param name="modelIdFilter">Filter by specific model ID (e.g., 'EnergyCommunity-1.0.0')</param>
    /// <returns>List of available CK types with basic metadata</returns>
    [McpServerTool(Name = "get_available_types")]
    [Description("Get all available Construction Kit types with their basic metadata")]
    public static async Task<object> GetAvailableTypes(
        IMcpServer server,
        bool includeAbstract = false,
        string? modelIdFilter = null)
    {
        try
        {
            var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
            var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

            // Get all available type graphs from the cache
            var availableTypes = new List<object>();

            // This is a simplified version - in reality you'd query the model repository
            // for all available models and their types
            var commonTypes = new[]
            {
                "System-1.0.0/Entity-1.0.0",
                "System-1.0.0/Query-1.0.0",
                "Basic-1.0.0/NamedEntity-1.0.0",
                "Basic-1.0.0/Document-1.0.0",
                "Basic-1.0.0/TreeNode-1.0.0",
                "Basic-1.0.0/Asset-1.0.0",
                "EnergyCommunity-1.0.0/Customer-1.0.0",
                "EnergyCommunity-1.0.0/MeteringPoint-1.0.0",
                "EnergyCommunity-1.0.0/OperatingFacility-1.0.0",
                "EnergyCommunity-1.0.0/BillingDocument-1.0.0",
                "Industry.Basic-1.0.0/Machine-1.0.0",
                "Industry.Basic-1.0.0/Event-1.0.0",
                "Industry.Basic-1.0.0/Alarm-1.0.0",
                "Industry.Energy-1.0.0/EnergyMeter-1.0.0",
                "Industry.Energy-1.0.0/Battery-1.0.0",
                "Industry.Energy-1.0.0/Inverter-1.0.0",
                "Industry.Maintenance-1.0.0/Order-1.0.0",
                "Industry.Maintenance-1.0.0/Employee-1.0.0"
            };

            foreach (var typeId in commonTypes)
            {
                if (!string.IsNullOrEmpty(modelIdFilter) && !typeId.StartsWith(modelIdFilter))
                    continue;

                try
                {
                    var typeGraph = await tenantRepository.GetCkTypeGraphAsync(new CkId<CkTypeId>(typeId));
                    var typeInfo = typeGraph.TypeWithAttributes;

                    if (!includeAbstract && typeInfo.IsAbstract)
                        continue;

                    availableTypes.Add(new
                    {
                        typeId = typeInfo.TypeId.ToString(),
                        modelId = typeInfo.TypeId.ModelId.ToString(),
                        typeName = typeInfo.TypeId.TypeName,
                        version = typeInfo.TypeId.Version.ToString(),
                        isAbstract = typeInfo.IsAbstract,
                        isFinal = typeInfo.IsFinal,
                        isCollectionRoot = typeInfo.IsCollectionRoot,
                        description = typeInfo.Description,
                        derivedFrom = typeInfo.DerivedFromCkTypeId?.ToString(),
                        attributeCount = typeInfo.Attributes?.Count ?? 0,
                        associationCount = typeInfo.Associations?.Count ?? 0
                    });
                }
                catch
                {
                    // Type might not be available in this tenant - skip
                    continue;
                }
            }

            return new
            {
                totalTypes = availableTypes.Count,
                includeAbstract,
                modelIdFilter,
                types = availableTypes.OrderBy(t => ((dynamic)t).typeId)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get available types",
                message = ex.Message
            };
        }
    }

    /// <summary>
    /// Get detailed schema information for a specific Construction Kit type
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="ckTypeId">Construction Kit Type ID</param>
    /// <param name="includeInherited">Include attributes and associations from parent types</param>
    /// <returns>Detailed type schema with attributes, associations, and metadata</returns>
    [McpServerTool(Name = "get_type_schema")]
    [Description("Get detailed schema information for a specific Construction Kit type")]
    public static async Task<object> GetTypeSchema(
        IMcpServer server,
        string ckTypeId,
        bool includeInherited = true)
    {
        try
        {
            var httpContextAccessor = server.Services!.GetRequiredService<IHttpContextAccessor>();
            var tenantRepository = await httpContextAccessor.GetTenantRepositoryAsync();

            var typeGraph = await tenantRepository.GetCkTypeGraphAsync(new CkId<CkTypeId>(ckTypeId));
            var typeInfo = typeGraph.TypeWithAttributes;

            // Format attributes
            var attributes = new List<object>();
            if (typeInfo.Attributes != null)
            {
                foreach (var attr in typeInfo.Attributes)
                {
                    attributes.Add(new
                    {
                        id = attr.Id.ToString(),
                        name = attr.Name,
                        isOptional = attr.IsOptional,
                        attributeInfo = typeGraph.AttributeGraphItems.ContainsKey(attr.Id) ? new
                        {
                            valueType = typeGraph.AttributeGraphItems[attr.Id].Attribute.ValueType.ToString(),
                            description = typeGraph.AttributeGraphItems[attr.Id].Attribute.Description,
                            defaultValues = typeGraph.AttributeGraphItems[attr.Id].Attribute.DefaultValues,
                            metaData = typeGraph.AttributeGraphItems[attr.Id].Attribute.MetaData?.ToDictionary(m => m.Key, m => m.Value)
                        } : null
                    });
                }
            }

            // Format associations
            var associations = new List<object>();
            if (typeInfo.Associations != null)
            {
                foreach (var assoc in typeInfo.Associations)
                {
                    associations.Add(new
                    {
                        id = assoc.Id.ToString(),
                        targetTypeId = assoc.TargetCkTypeId.ToString(),
                        roleInfo = typeGraph.AssociationRoleGraphItems.ContainsKey(assoc.Id) ? new
                        {
                            inboundName = typeGraph.AssociationRoleGraphItems[assoc.Id].AssociationRole.InboundName,
                            outboundName = typeGraph.AssociationRoleGraphItems[assoc.Id].AssociationRole.OutboundName,
                            inboundMultiplicity = typeGraph.AssociationRoleGraphItems[assoc.Id].AssociationRole.InboundMultiplicity.ToString(),
                            outboundMultiplicity = typeGraph.AssociationRoleGraphItems[assoc.Id].AssociationRole.OutboundMultiplicity.ToString()
                        } : null
                    });
                }
            }

            // Get inheritance hierarchy
            var hierarchy = new List<string>();
            var currentTypeId = typeInfo.DerivedFromCkTypeId;
            while (currentTypeId != null)
            {
                hierarchy.Add(currentTypeId.ToString());
                try
                {
                    var parentGraph = await tenantRepository.GetCkTypeGraphAsync(currentTypeId);
                    currentTypeId = parentGraph.TypeWithAttributes.DerivedFromCkTypeId;
                }
                catch
                {
                    break;
                }
            }

            return new
            {
                typeId = typeInfo.TypeId.ToString(),
                modelId = typeInfo.TypeId.ModelId.ToString(),
                typeName = typeInfo.TypeId.TypeName,
                version = typeInfo.TypeId.Version.ToString(),
                isAbstract = typeInfo.IsAbstract,
                isFinal = typeInfo.IsFinal,
                isCollectionRoot = typeInfo.IsCollectionRoot,
                isStreamType = typeInfo.IsStreamType,
                description = typeInfo.Description,
                derivedFrom = typeInfo.DerivedFromCkTypeId?.ToString(),
                inheritanceHierarchy = hierarchy,
                indexes = typeInfo.Indexes?.Select(idx => new
                {
                    indexType = idx.IndexType.ToString(),
                    language = idx.Language,
                    fields = idx.Fields?.Select(f => new
                    {
                        weight = f.Weight,
                        attributePaths = f.AttributePaths
                    })
                }),
                attributes = attributes,
                associations = associations,
                schema = new
                {
                    canCreate = !typeInfo.IsAbstract,
                    canQuery = typeInfo.IsCollectionRoot || !typeInfo.IsAbstract,
                    requiredAttributes = attributes.Where(a => !((dynamic)a).isOptional).Select(a => ((dynamic)a).name),
                    optionalAttributes = attributes.Where(a => ((dynamic)a).isOptional).Select(a => ((dynamic)a).name)
                }
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get type schema",
                message = ex.Message,
                ckTypeId
            };
        }
    }

    /// <summary>
    /// Get all models available in the system
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <returns>List of available Construction Kit models</returns>
    [McpServerTool(Name = "get_available_models")]
    [Description("Get all available Construction Kit models in the system")]
    public static async Task<object> GetAvailableModels(IMcpServer server)
    {
        try
        {
            // This would typically query the model repository for available models
            var models = new[]
            {
                new { modelId = "System-1.0.0", description = "Core system types and infrastructure" },
                new { modelId = "Basic-1.0.0", description = "Basic domain types like entities, documents, and tree nodes" },
                new { modelId = "EnergyCommunity-1.0.0", description = "Energy community management with customers, metering, and billing" },
                new { modelId = "Industry.Basic-1.0.0", description = "Industrial automation basics with machines, events, and alarms" },
                new { modelId = "Industry.Energy-1.0.0", description = "Energy industry specifics with meters, batteries, and inverters" },
                new { modelId = "Industry.Fluid-1.0.0", description = "Fluid measurement and management systems" },
                new { modelId = "Industry.Maintenance-1.0.0", description = "Maintenance management with orders, employees, and costs" },
                new { modelId = "Environment-1.0.0", description = "Environmental monitoring and waste management" },
                new { modelId = "System.Identity-1.0.0", description = "Identity and access management" },
                new { modelId = "System.Communication-1.0.0", description = "Communication adapters and data pipelines" },
                new { modelId = "System.Notification-1.0.0", description = "Event notifications and templates" }
            };

            return new
            {
                totalModels = models.Length,
                models = models.OrderBy(m => m.modelId)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to get available models",
                message = ex.Message
            };
        }
    }

    /// <summary>
    /// Search for types by name or description
    /// </summary>
    /// <param name="server">MCP Server instance</param>
    /// <param name="searchTerm">Search term to look for in type names or descriptions</param>
    /// <param name="includeAbstract">Include abstract types in search results</param>
    /// <returns>Matching types</returns>
    [McpServerTool(Name = "search_types")]
    [Description("Search for Construction Kit types by name or description")]
    public static async Task<object> SearchTypes(
        IMcpServer server,
        string searchTerm,
        bool includeAbstract = false)
    {
        try
        {
            var allTypesResult = await GetAvailableTypes(server, includeAbstract);
            var allTypes = ((dynamic)allTypesResult).types as IEnumerable<dynamic>;

            if (allTypes == null)
            {
                return new { error = "Failed to get types for search" };
            }

            var matchingTypes = allTypes.Where(t =>
                ((string)t.typeId).Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                ((string)t.typeName).Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty((string)t.description) && ((string)t.description).Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            return new
            {
                searchTerm,
                matchCount = matchingTypes.Count,
                includeAbstract,
                matches = matchingTypes
            };
        }
        catch (Exception ex)
        {
            return new
            {
                error = "Failed to search types",
                message = ex.Message,
                searchTerm
            };
        }
    }
}
