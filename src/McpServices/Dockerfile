FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8443

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE
ARG OCTO_VERSION
ARG OCTO_CORE_LIB_VERSION
ARG OCTO_COMM_VERSION

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /src
COPY . .
ENV NUGET_XMLDOC_MODE=none
WORKDIR "/src/src/McpServices"
# Each `${VAR:+-p:Name=${VAR}}` only emits its -p switch when VAR is
# non-empty. The three versions are guarded INDEPENDENTLY: on an r-tag build
# OCTO_VERSION is set but OCTO_CORE_LIB_VERSION / OCTO_COMM_VERSION are
# intentionally blank (pipeline-templates tpl-v0.4.7 leaves them unset so the
# repo's Directory.Build.props pin wins). Emitting `-p:OctoCoreLibVersion=`
# (blank) would set an empty MSBuild GLOBAL property that the .props
# `Condition="'$(OctoCoreLibVersion)'==''"` fallback cannot override → NU1015.
# Guarding per-variable omits the switch entirely so the pin applies.
RUN dotnet build "./McpServices.csproj" -o /app/build -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} ${OCTO_VERSION:+-p:OctoVersion=${OCTO_VERSION}} ${OCTO_CORE_LIB_VERSION:+-p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION}} ${OCTO_COMM_VERSION:+-p:OctoCommVersion=${OCTO_COMM_VERSION}}

FROM build AS publish
RUN dotnet publish "./McpServices.csproj" -o /app/publish /p:UseAppHost=false -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} ${OCTO_VERSION:+-p:OctoVersion=${OCTO_VERSION}} ${OCTO_CORE_LIB_VERSION:+-p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION}} ${OCTO_COMM_VERSION:+-p:OctoCommVersion=${OCTO_COMM_VERSION}}

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Backend.McpServices.dll"]
