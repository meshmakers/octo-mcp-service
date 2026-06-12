namespace Meshmakers.Octo.Backend.McpServices.Services;

/// <summary>
///     Canonical content templates the <c>apply_custom_app_scaffold</c> tool expands into
///     <c>WriteOp</c> entries. Each template is a raw string with <c>&lt;&lt;Token&gt;&gt;</c>
///     placeholders the renderer fills in. The chevron syntax avoids collision with
///     Angular's <c>{{ }}</c> interpolation in the HTML template.
///     <para>
///         The templates are anchored to <c>meshmakers/template-repo</c>'s conventions
///         (Angular 21 standalone, signals, Kendo Grid + Apollo + DatePipe). The renderer
///         tests pin every expansion against a golden expected output so template drift
///         (template-repo refactor, our handful of constants getting out of sync) gets
///         caught at build time.
///     </para>
///     <para>
///         Placeholder vocabulary:
///         <list type="bullet">
///             <item><c>&lt;&lt;ClassName&gt;&gt;</c> — PascalCase page name, e.g. <c>AuditLog</c>.</item>
///             <item><c>&lt;&lt;RouteSlug&gt;&gt;</c> — kebab-case route slug, e.g. <c>audit-log</c>.</item>
///             <item><c>&lt;&lt;CamelClass&gt;&gt;</c> — camelCase, e.g. <c>auditLog</c>.</item>
///             <item><c>&lt;&lt;TypeId&gt;&gt;</c> — full CK type id (header comment only).</item>
///             <item><c>&lt;&lt;GraphqlOperation&gt;&gt;</c> — operation under <c>runtime.</c> (e.g. <c>systemAiAuditEvent</c>).</item>
///             <item><c>&lt;&lt;QueryName&gt;&gt;</c> — GraphQL query name (e.g. <c>GetAuditLog</c>).</item>
///             <item><c>&lt;&lt;QueryFile&gt;&gt;</c> — query filename without extension (e.g. <c>getAuditLog</c>).</item>
///             <item><c>&lt;&lt;ModelFile&gt;&gt;</c> — DTO filename without extension (e.g. <c>audit-log-entry</c>).</item>
///             <item><c>&lt;&lt;ModelName&gt;&gt;</c> — DTO interface name (e.g. <c>AuditLogEntry</c>).</item>
///             <item><c>&lt;&lt;AttributesAsGraphqlLeaves&gt;&gt;</c> — multi-line block of GraphQL leaf names, indented 10 spaces. Empty binding → single <c>            # TODO: list the attributes to fetch</c> line.</item>
///             <item><c>&lt;&lt;AttributesAsDtoFields&gt;&gt;</c> — multi-line block of DTO interface fields, indented 2 spaces.</item>
///             <item><c>&lt;&lt;AttributesAsMapAssignments&gt;&gt;</c> — multi-line block inside <c>mapXxxResult</c>, indented 6 spaces.</item>
///         </list>
///     </para>
/// </summary>
public static class CustomAppTemplates
{
    /// <summary>Standalone page component — signals + inject + Kendo Grid + DatePipe.</summary>
    public const string PageComponentTs = """
        import { Component, OnInit, inject, signal } from '@angular/core';
        import { DatePipe } from '@angular/common';
        import { GridModule } from '@progress/kendo-angular-grid';
        import { <<ClassName>>Service } from '../../services/<<RouteSlug>>.service';
        import { <<ModelName>> } from '../../models/<<ModelFile>>';

        @Component({
          selector: 'app-<<RouteSlug>>',
          standalone: true,
          imports: [GridModule, DatePipe],
          templateUrl: './<<RouteSlug>>.html',
          styleUrl: './<<RouteSlug>>.scss',
        })
        export class <<ClassName>>Component implements OnInit {
          private readonly <<CamelClass>>Service = inject(<<ClassName>>Service);

          readonly entries = signal<<<ModelName>>[]>([]);
          readonly loading = signal(false);

          ngOnInit(): void {
            this.load();
          }

          load(): void {
            this.loading.set(true);
            this.<<CamelClass>>Service.fetch<<ClassName>>().subscribe((entries) => {
              this.entries.set(entries);
              this.loading.set(false);
            });
          }
        }

        """;

    /// <summary>Kendo Grid skeleton — refresh button + TODO column markers.</summary>
    public const string PageTemplateHtml = """
        <div class="flex items-center justify-between mb-4">
          <h1 class="text-xl font-semibold"><<ClassName>></h1>
          <button kendoButton (click)="load()" [disabled]="loading()">Refresh</button>
        </div>

        <kendo-grid
          [data]="entries()"
          [loading]="loading()"
          [pageable]="false"
          [sortable]="true">
          <!-- TODO: add <kendo-grid-column> rows for the attributes you want to surface.   -->
          <!-- Example shape:                                                               -->
          <!--   <kendo-grid-column field="eventType" title="Event Type"></kendo-grid-column>-->
          <!--   <kendo-grid-column field="at" title="Timestamp">                            -->
          <!--     <ng-template kendoGridCellTemplate let-row>{{ row.at | date }}</ng-template> -->
          <!--   </kendo-grid-column>                                                         -->
        </kendo-grid>

        """;

    /// <summary>Empty styles file — intentionally blank.</summary>
    public const string PageStylesScss = "";

    /// <summary>Apollo-Angular service — fetch + extracted pure-function mapper.</summary>
    public const string ServiceTs = """
        import { Injectable, inject } from '@angular/core';
        import { Observable, of } from 'rxjs';
        import { catchError, map } from 'rxjs/operators';
        import { <<QueryName>>GQL, <<QueryName>>Query } from '../graphQL/<<QueryFile>>.generated';
        import { <<ModelName>> } from '../models/<<ModelFile>>';

        /**
         * Loads <<TypeId>> records for the /<<RouteSlug>> page via the Runtime GraphQL API.
         * Mirrors the Apollo pattern used by MaintenanceModeService.
         */
        @Injectable({
          providedIn: 'root',
        })
        export class <<ClassName>>Service {
          private readonly <<CamelClass>>GQL = inject(<<QueryName>>GQL);

          fetch<<ClassName>>(first = 100): Observable<<<ModelName>>[]> {
            return this.<<CamelClass>>GQL
              .fetch({ variables: { first }, fetchPolicy: 'network-only' })
              .pipe(
                map((result) => map<<ClassName>>Result(result.data)),
                catchError(() => of<<<ModelName>>[]>([])),
              );
          }
        }

        /**
         * Maps the GraphQL connection result to a flat list of <<ModelName>> rows.
         * Extracted as a pure function so it can be unit-tested without Angular DI.
         */
        export function map<<ClassName>>Result(
          data: <<QueryName>>Query | null | undefined,
        ): <<ModelName>>[] {
          const edges = data?.runtime?.<<GraphqlOperation>>?.edges ?? [];
          return edges
            .map((edge) => edge?.node)
            .filter((node): node is NonNullable<typeof node> => node != null)
            .map((node) => ({
        <<AttributesAsMapAssignments>>
            }));
        }

        """;

    /// <summary>Vitest spec for the extracted mapper.</summary>
    public const string ServiceSpecTs = """
        import { describe, it, expect } from 'vitest';
        import { map<<ClassName>>Result } from './<<RouteSlug>>.service';

        describe('map<<ClassName>>Result', () => {
          it('returns empty array when data is null', () => {
            expect(map<<ClassName>>Result(null)).toEqual([]);
          });

          it('returns empty array when data is undefined', () => {
            expect(map<<ClassName>>Result(undefined)).toEqual([]);
          });

          it('returns empty array when edges list is missing', () => {
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            expect(map<<ClassName>>Result({ runtime: { <<GraphqlOperation>>: {} } } as any)).toEqual([]);
          });

          it('drops null nodes', () => {
            // eslint-disable-next-line @typescript-eslint/no-explicit-any
            const result = map<<ClassName>>Result({
              runtime: { <<GraphqlOperation>>: { edges: [null, { node: null }] } },
            } as any);
            expect(result).toEqual([]);
          });
        });

        """;

    /// <summary>DTO interface — fields from the binding's attributes.</summary>
    public const string ModelTs = """
        /**
         * DTO for one row of the /<<RouteSlug>> page. Sourced from the CK type <<TypeId>>
         * via the GraphQL query in graphQL/<<QueryFile>>.graphql.
         */
        export interface <<ModelName>> {
        <<AttributesAsDtoFields>>
        }

        """;

    /// <summary>GraphQL query stub — leaves from the binding's attributes + TODO when empty.</summary>
    public const string QueryGraphql = """
        query <<QueryName>>($first: Int) {
          runtime {
            <<GraphqlOperation>>(first: $first) {
              edges {
                node {
                  rtId
        <<AttributesAsGraphqlLeaves>>
                }
              }
            }
          }
        }

        """;
}
