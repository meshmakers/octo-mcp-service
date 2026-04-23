# OctoMesh MCP Service

A comprehensive Model Context Protocol (MCP) server for OctoMesh Construction Kit operations, providing AI assistants with powerful tools to interact with your data mesh infrastructure.

## 🚀 Features

### **Dynamic CRUD Operations**
- **Universal Entity Management**: Create, read, update, and delete entities for any Construction Kit type
- **Type-Safe Operations**: Automatic validation based on CK schemas
- **Intelligent Filtering**: Advanced query capabilities with pagination and sorting
- **Association Management**: Navigate relationships between entities

### **Schema Discovery & Exploration**
- **Real-time Schema Discovery**: Explore available CK types and their schemas
- **Interactive Documentation**: Get detailed information about types, attributes, and associations
- **Search & Filtering**: Find types by name, description, or model
- **Validation Support**: Validate parameters before execution

### **Domain-Specific Analytics**
- **Energy Community Tools**: Analyze consumption patterns, billing documents, and efficiency metrics
- **Industrial IoT Monitoring**: Track machine performance, alarms, and operational status
- **Maintenance Management**: Monitor orders, costs, and resource allocation
- **Advanced Reporting**: Generate executive dashboards and forecasting reports

### **Tool Management & Monitoring**
- **Usage Statistics**: Track tool performance and usage patterns
- **Error Analytics**: Monitor failures and common issues
- **Health Monitoring**: Built-in health checks and status reporting
- **Parameter Validation**: Pre-execution validation with helpful error messages

## 🔧 Installation & Setup

### **Prerequisites**
- .NET 8.0 or later
- MongoDB instance
- OctoMesh Runtime Services

### **Configuration**

1. **Update appsettings.json**:
```json
{
  "DynamicTools": {
    "EnableDynamicToolGeneration": true,
    "MaxQueryResultLimit": 1000,
    "EnableToolStatistics": true,
    "DomainTools": {
      "EnableEnergyTools": true,
      "EnableIndustryTools": true,
      "EnableAnalyticsTools": true
    }
  },
  "Runtime": {
    "MongoDB": {
      "ConnectionString": "mongodb://localhost:27017",
      "DatabaseNamePrefix": "octo_"
    }
  }
}
```

2. **Start the Service**:
```bash
cd src/McpServices
dotnet run
```

3. **Configure Claude Desktop**:
```json
{
  "mcpServers": {
    "octo-mesh": {
      "command": "node",
      "args": ["src/mcp-bridge.js"],
      "cwd": "/path/to/octo-mcp-service"
    }
  }
}
```

## 🛠️ Available Tools

### **CRUD Operations**
- `query_entities` - Query entities with filtering and pagination
- `get_entity_by_id` - Retrieve a specific entity by ID
- `create_entity` - Create new entities with validation
- `update_entity` - Update existing entities
- `delete_entity` - Remove entities from the system

### **Schema Discovery**
- `get_available_types` - List all available CK types
- `get_type_schema` - Get detailed schema for a specific type
- `get_available_models` - List all CK models
- `search_types` - Search types by name or description

### **Energy Community**
- `analyze_energy_consumption` - Analyze consumption patterns over time
- `get_billing_documents` - Retrieve billing information for customers

### **Industrial Operations**
- `get_machine_alarms` - Monitor active alarms and alerts
- `get_machine_status` - Check machine operational status
- `get_maintenance_orders` - Track maintenance activities

### **Advanced Analytics**
- `generate_energy_efficiency_report` - Comprehensive efficiency analysis
- `analyze_machine_performance` - Performance and downtime analysis
- `analyze_maintenance_costs` - Cost analysis and forecasting
- `generate_executive_dashboard` - High-level KPI dashboard

### **Tool Management**
- `list_available_tools` - Get information about all tools
- `get_tool_details` - Detailed tool documentation
- `get_tool_statistics` - Usage and performance metrics
- `validate_tool_parameters` - Pre-execution parameter validation

## 📖 Usage Examples

### **Query Energy Customers**
```json
{
  "tool": "query_entities",
  "parameters": {
    "ckTypeId": "EnergyCommunity-1.0.0/Customer-1.0.0",
    "filters": "{\"State\": \"Active\"}",
    "limit": 50
  }
}
```

### **Analyze Energy Consumption**
```json
{
  "tool": "analyze_energy_consumption",
  "parameters": {
    "fromDate": "2024-01-01T00:00:00Z",
    "toDate": "2024-01-31T23:59:59Z",
    "facilityId": "12345"
  }
}
```

### **Create New Customer**
```json
{
  "tool": "create_entity",
  "parameters": {
    "ckTypeId": "EnergyCommunity-1.0.0/Customer-1.0.0",
    "entityData": "{\"CustomerNumber\": \"CUST-001\", \"Contact\": {...}, \"State\": \"Active\"}"
  }
}
```

### **Get Machine Alarms**
```json
{
  "tool": "get_machine_alarms",
  "parameters": {
    "priorityLevel": "High",
    "alarmState": "Unacknowledged"
  }
}
```

## 🏗️ Architecture

### **Service Architecture**
```
┌─────────────────────┐
│   Claude Desktop    │
├─────────────────────┤
│   MCP Bridge        │
├─────────────────────┤
│   MCP Server        │
│   - Tool Discovery  │
│   - Execution       │
│   - Validation      │
├─────────────────────┤
│   Domain Services   │
│   - Dynamic CRUD    │
│   - Analytics       │
│   - Monitoring      │
├─────────────────────┤
│   OctoMesh Runtime  │
│   - CK Engine       │
│   - MongoDB Repo    │
└─────────────────────┘
```

### **Tool Categories**
- **CRUD Tools**: Universal entity operations for all CK types
- **Schema Tools**: Discovery and exploration of data models
- **Domain Tools**: Business-specific analytics and reporting
- **Analytics Tools**: Advanced reporting and forecasting
- **Management Tools**: Tool monitoring and administration

### **Caching Strategy**
- **CK Type Graphs**: Cached for 30 minutes (configurable)
- **Available Types**: Per-tenant caching
- **Tool Statistics**: In-memory aggregation
- **Performance Metrics**: Real-time collection

## 🔧 Configuration Options

### **Dynamic Tool Options**
| Setting | Default | Description |
|---------|---------|-------------|
| `EnableDynamicToolGeneration` | `true` | Enable automatic tool generation |
| `MaxQueryResultLimit` | `1000` | Maximum entities per query |
| `DefaultQueryLimit` | `100` | Default result limit |
| `AnalyticsTimeoutSeconds` | `300` | Timeout for long operations |
| `EnableToolStatistics` | `true` | Collect usage statistics |
| `CkTypeGraphCacheDurationMinutes` | `30` | Cache duration for schemas |

### **Domain Tool Options**
| Setting | Default | Description |
|---------|---------|-------------|
| `EnableEnergyTools` | `true` | Energy community analytics |
| `EnableIndustryTools` | `true` | Industrial IoT tools |
| `EnableAnalyticsTools` | `true` | Advanced reporting |
| `EnableEnvironmentTools` | `false` | Environmental monitoring |
| `MaxAnalyticsDateRangeDays` | `365` | Maximum date range |
| `EnableForecasting` | `true` | Predictive analytics |

## 📊 Monitoring & Health Checks

### **Health Endpoints**
- `/health` - Overall service health
- `/health/ready` - Readiness probe
- `/health/live` - Liveness probe

### **Tool Statistics**
Access real-time tool usage statistics:
```json
{
  "tool": "get_tool_statistics",
  "parameters": {
    "timeRange": "day"
  }
}
```

### **Performance Metrics**
- Execution times per tool
- Success/failure rates
- Cache hit ratios
- Error categorization

## 🔐 Authentication & Multi-Tenant Access

### **OAuth2 Device Authorization Flow**

The MCP server supports authentication via the OAuth2 Device Authorization Flow, ideal for CLI and AI clients (e.g., Claude Code). No browser redirect required on the client side.

**Flow:**
1. Client calls `authenticate` tool
2. Server returns a user code and verification URL
3. User opens the URL in a browser, enters the code, and logs in
4. Client calls `check_auth_status` to complete authentication
5. Server stores tokens per MCP session (automatic refresh)

**Identity Server Client:** `octo-mcpServices-device` (registered automatically on startup)

### **Tenant Resolution (Stateless)**

Tenants are resolved per-request using this priority:
1. **Tool parameter `tenantId`** (explicit, from any endpoint)
2. **Route parameter `{tenantId}`** (from `/{tenantId}/mcp` endpoint)
3. Error if no tenant can be resolved

This is fully stateless — no tenant is stored in session state. The AI client remembers the tenant in its conversation context.

### **Endpoints**

| Endpoint | Description |
|----------|-------------|
| `/{tenantId}/mcp` | Tenant-scoped MCP endpoint (backwards compatible) |
| `/mcp` | Tenantless MCP endpoint (tenant via tool parameter) |

### **Claude Code Configuration (single entry)**

```json
{
  "mcpServers": {
    "octomesh": {
      "type": "http",
      "url": "https://mcp.example.com/mcp"
    }
  }
}
```

### **Authentication & Identity Tools**

| Tool | Auth Required | Description |
|------|---------------|-------------|
| `authenticate` | No | Start Device Authorization Flow |
| `check_auth_status` | No | Check if user completed browser authentication |
| `whoami` | Yes | User info (name, email, roles, tenants) |
| `list_tenants` | Yes | List all tenants the user has access to |

## 🔒 Security & Permissions

### **Tenant Isolation**
- All operations are tenant-scoped
- Tenant resolution from tool parameter or URL path
- Isolated data access per tenant
- `allowed_tenants` JWT claim validation

### **Parameter Validation**
- Type-safe parameter validation
- Schema-based entity validation
- SQL injection prevention
- Input sanitization

### **Error Handling**
- Detailed errors in development
- Sanitized errors in production
- Structured error responses
- Error correlation IDs

## 🚀 Development

### **Adding New Tools**
1. Create a new tool class with `[McpServerToolType]` attribute
2. Add tool methods with `[McpServerTool]` attribute
3. Register in `Program.cs` if needed
4. Update documentation

### **Custom Domain Tools**
```csharp
[McpServerToolType]
public sealed class CustomDomainTools
{
    [McpServerTool(Name = "my_custom_tool")]
    [Description("Custom business logic tool")]
    public static async Task<object> MyCustomTool(
        IMcpServer server,
        string parameter1,
        int parameter2 = 10)
    {
        // Implementation
    }
}
```

### **Testing**
```bash
# Run unit tests
dotnet test

# Start development server
dotnet run --environment Development

# Test with Claude Desktop
# Configure MCP server and interact via Claude
```

## 📝 Changelog

### **Version 1.1.0**
- OAuth2 Device Authorization Flow for CLI/AI client authentication
- Tenantless `/mcp` endpoint with per-tool `tenantId` parameter
- `authenticate`, `check_auth_status`, `whoami`, `list_tenants` tools
- Identity Server client registration (`octo-mcpServices-device`, `octo-mcpServices-swagger`)
- Session-based token management with automatic refresh
- `ITenantResolutionService` for stateless multi-tenant tool access
- All existing tools extended with optional `tenantId` parameter

### **Version 1.0.0**
- ✅ Dynamic CRUD operations for all CK types
- ✅ Schema discovery and exploration tools
- ✅ Energy community analytics
- ✅ Industrial IoT monitoring
- ✅ Maintenance management tools
- ✅ Advanced analytics and reporting
- ✅ Tool management and statistics
- ✅ Health monitoring and validation
- ✅ Comprehensive configuration options
- ✅ Multi-tenant support

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch
3. Implement changes with tests
4. Update documentation
5. Submit a pull request

## 📄 License

This project is licensed under the MIT License - see the LICENSE file for details.

## 🆘 Support

For support and questions:
- Check the tool documentation: `get_tool_details`
- Monitor tool statistics: `get_tool_statistics`
- Review health status: `/health`
- Contact the development team

---

**Built with ❤️ for the OctoMesh ecosystem**
