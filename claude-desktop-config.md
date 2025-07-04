# Claude Desktop MCP Configuration

This file contains example configurations for connecting Claude Desktop to the OctoMesh MCP Service.

## Option 1: Local Development Setup

Add this to your Claude Desktop config file:

**macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`
**Windows**: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "octomesh-local": {
      "command": "node",
      "args": ["src/mcp-bridge.js"],
      "cwd": "/Users/gerald/RiderProjects/meshmakers/octo-mcp-service",
      "env": {
        "NODE_TLS_REJECT_UNAUTHORIZED": "0"
      }
    }
  }
}
```

## Option 2: Direct HTTP Connection (Alternative)

If you prefer to connect directly without the bridge:

```json
{
  "mcpServers": {
    "octomesh-direct": {
      "command": "curl",
      "args": [
        "-X", "POST",
        "-H", "Content-Type: application/json",
        "-d", "@-",
        "https://localhost:5017/{tenantId}/mcp"
      ],
      "env": {
        "TENANT_ID": "your-tenant-id"
      }
    }
  }
}
```

## Option 3: Production Setup

For production environments:

```json
{
  "mcpServers": {
    "octomesh-prod": {
      "command": "node",
      "args": ["src/mcp-bridge.js"],
      "cwd": "/path/to/production/octo-mcp-service",
      "env": {
        "OCTO_ENVIRONMENT": "Production",
        "OCTO_TENANT_ID": "your-tenant-id",
        "OCTO_API_BASE_URL": "https://your-domain.com"
      }
    }
  }
}
```

## Configuration Options

### Environment Variables

- `NODE_TLS_REJECT_UNAUTHORIZED`: Set to "0" for development with self-signed certificates
- `OCTO_ENVIRONMENT`: "Development", "Staging", or "Production"
- `OCTO_TENANT_ID`: Your tenant identifier
- `OCTO_API_BASE_URL`: Base URL for the MCP service
- `OCTO_MONGODB_CONNECTION`: MongoDB connection string override
- `OCTO_LOG_LEVEL`: Logging level (Debug, Information, Warning, Error)

### MCP Bridge Configuration

The `mcp-bridge.js` file can be configured by modifying these settings:

```javascript
const options = {
  hostname: 'localhost',  // OctoMesh MCP service host
  port: 5017,            // OctoMesh MCP service port
  path: '/sbeg/mcp',     // MCP endpoint path
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'Accept': 'application/json, text/event-stream',
    'Cache-Control': 'no-cache'
  }
};
```

## Testing the Connection

After configuring Claude Desktop:

1. **Restart Claude Desktop**
2. **Test basic connectivity**:
   ```
   Can you list the available MCP tools?
   ```

3. **Test schema discovery**:
   ```
   What Construction Kit types are available in the system?
   ```

4. **Test a simple query**:
   ```
   Query some customer entities from the energy community model
   ```

## Troubleshooting

### Common Issues

1. **Connection Refused**
   - Ensure the OctoMesh MCP service is running
   - Check the port number (default: 5017)
   - Verify firewall settings

2. **SSL/TLS Errors**
   - Set `NODE_TLS_REJECT_UNAUTHORIZED=0` for development
   - Use proper certificates for production

3. **Tenant Not Found**
   - Verify the tenant ID in the URL path
   - Ensure the tenant exists in the system

4. **Permission Denied**
   - Check authentication configuration
   - Verify user permissions for the tenant

### Debug Mode

Enable debug logging by setting:

```json
{
  "mcpServers": {
    "octomesh-debug": {
      "command": "node",
      "args": ["src/mcp-bridge.js"],
      "cwd": "/path/to/octo-mcp-service",
      "env": {
        "DEBUG": "true",
        "OCTO_LOG_LEVEL": "Debug"
      }
    }
  }
}
```

### Logs Location

Check these locations for logs:

- **Service Logs**: Check the console output of the .NET service
- **Bridge Logs**: Node.js console output
- **Claude Desktop Logs**: 
  - macOS: `~/Library/Logs/Claude/`
  - Windows: `%APPDATA%\Claude\Logs\`

## Example Interactions

Once connected, you can interact with OctoMesh using natural language:

### Schema Discovery
```
"Show me all available Construction Kit types"
"What attributes does the Customer type have?"
"Search for types related to energy"
```

### Data Operations
```
"Query the first 10 customers from the energy community"
"Create a new customer with name 'Test Customer'"
"Show me all active machines"
"Get billing documents for customer CUST-001"
```

### Analytics
```
"Analyze energy consumption for the last month"
"Show me all high-priority machine alarms"
"Generate an executive dashboard for this quarter"
"What maintenance orders are overdue?"
```

### Tool Management
```
"What tools are available?"
"Show me usage statistics for the query tools"
"Validate these parameters for creating a customer"
```

## Advanced Configuration

### Multiple Tenants

To work with multiple tenants, configure separate MCP servers:

```json
{
  "mcpServers": {
    "octomesh-tenant-a": {
      "command": "node",
      "args": ["src/mcp-bridge.js"],
      "cwd": "/path/to/octo-mcp-service",
      "env": {
        "TENANT_ID": "tenant-a"
      }
    },
    "octomesh-tenant-b": {
      "command": "node", 
      "args": ["src/mcp-bridge.js"],
      "cwd": "/path/to/octo-mcp-service",
      "env": {
        "TENANT_ID": "tenant-b"
      }
    }
  }
}
```

### Custom Tool Configuration

To enable/disable specific tool categories:

```json
{
  "mcpServers": {
    "octomesh-analytics-only": {
      "command": "node",
      "args": ["src/mcp-bridge.js"],
      "cwd": "/path/to/octo-mcp-service",
      "env": {
        "OCTO_DynamicTools__DomainTools__EnableEnergyTools": "true",
        "OCTO_DynamicTools__DomainTools__EnableIndustryTools": "false",
        "OCTO_DynamicTools__DomainTools__EnableAnalyticsTools": "true"
      }
    }
  }
}
```

Environment variables follow the pattern: `OCTO_SectionName__SubSection__PropertyName`
