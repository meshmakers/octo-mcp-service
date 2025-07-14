using System.Collections;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.McpServices.Models;

/// <summary>
/// Maps between runtime entities and DTOs
/// </summary>
public static class EntityMapper
{
    /// <summary>
    /// Maps a collection of RtEntity objects to RtEntityDto objects
    /// </summary>
    /// <param name="entities">Source entities</param>
    /// <param name="ckCacheService">Construction Kit cache service</param>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>Mapped DTOs</returns>
    public static IList<RtEntityDto> MapToDto(IEnumerable<RtEntity> entities, ICkCacheService ckCacheService, string tenantId)
    {
        return entities.Select(entity => MapToDto(entity, ckCacheService, tenantId)).ToList();
    }

    /// <summary>
    /// Maps a single RtEntity to RtEntityDto
    /// </summary>
    /// <param name="entity">Source entity</param>
    /// <param name="ckCacheService">Construction Kit cache service</param>
    /// <param name="tenantId">Tenant ID</param>
    /// <returns>Mapped DTO</returns>
    public static RtEntityDto MapToDto(RtEntity entity, ICkCacheService ckCacheService, string tenantId)
    {
        var dto = new RtEntityDto
        {
            RtId = entity.RtId,
            RtCreationDateTime = entity.RtCreationDateTime,
            RtChangedDateTime = entity.RtChangedDateTime,
            CkTypeId = entity.CkTypeId ?? throw McpServerException.CkTypeIdNotSet(entity.RtId, tenantId),
            RtWellKnownName = entity.RtWellKnownName,
            RtVersion = entity.RtVersion
        };

        // Map attributes
        if (entity.Attributes.Any())
        {
            dto.Attributes = entity.Attributes.Select(attr => new RtEntityAttributeDto
            {
                AttributeName = attr.Key,
                Value = MapAttributeValue(attr.Value)
            }).ToList();
        }

        return dto;
    }

    /// <summary>
    /// Maps attribute values to appropriate types for DTOs
    /// </summary>
    /// <param name="attributeValue">The attribute value</param>
    /// <returns>Mapped value</returns>
    private static object? MapAttributeValue(object? attributeValue)
    {
        if (attributeValue == null)
            return null;

        return attributeValue switch
        {
            // Handle RtRecord
            RtRecord rtRecord => MapRtRecordToDto(rtRecord),
            
            // Handle collections of RtRecords
            IEnumerable enumerable when IsRtRecordCollection(enumerable) => 
                enumerable.Cast<RtRecord>().Select(MapRtRecordToDto).ToList(),
            
            // Handle other collections
            IEnumerable<object> collection => collection.ToList(),
            
            // Handle primitive types and other objects
            _ => attributeValue
        };
    }

    /// <summary>
    /// Maps an RtRecord to RtRecordDto
    /// </summary>
    /// <param name="rtRecord">Source record</param>
    /// <returns>Mapped DTO</returns>
    private static RtRecordDto MapRtRecordToDto(RtRecord rtRecord)
    {
        var dto = new RtRecordDto
        {
            CkRecordId = rtRecord.CkRecordId
        };

        // Map record attributes
        if (rtRecord.Attributes.Any())
        {
            dto.Attributes = rtRecord.Attributes.Select(attr => new RtEntityAttributeDto
            {
                AttributeName = attr.Key,
                Value = MapAttributeValue(attr.Value)
            }).ToList();
        }

        return dto;
    }

    /// <summary>
    /// Checks if an enumerable contains RtRecord objects
    /// </summary>
    /// <param name="enumerable">The enumerable to check</param>
    /// <returns>True if it contains RtRecord objects</returns>
    private static bool IsRtRecordCollection(IEnumerable enumerable)
    {
        var enumerator = enumerable.GetEnumerator();
        if (enumerator.MoveNext())
        {
            return enumerator.Current is RtRecord;
        }
        return false;
    }
}
