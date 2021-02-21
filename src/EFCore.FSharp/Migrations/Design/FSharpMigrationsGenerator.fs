﻿namespace EntityFrameworkCore.FSharp.Migrations.Design

open System

open Microsoft.EntityFrameworkCore.Metadata
open Microsoft.EntityFrameworkCore.Migrations.Operations
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations.Design

open EntityFrameworkCore.FSharp.EntityFrameworkExtensions
open EntityFrameworkCore.FSharp.IndentedStringBuilderUtilities

type FSharpMigrationsGenerator(dependencies, fSharpDependencies : FSharpMigrationsGeneratorDependencies) =
    inherit MigrationsCodeGenerator(dependencies)

    let code = fSharpDependencies.FSharpHelper
    let generator = fSharpDependencies.FSharpMigrationOperationGenerator
    let snapshot = fSharpDependencies.FSharpSnapshotGenerator

    // Due to api shape we're currently forced to work around the fact EF expects 2 files per migration
    let mutable tempUpOperations = list.Empty
    let mutable tempDownOperations = list.Empty
    let mutable tempMigrationName = String.Empty

    let writeCreateTableType (sb: IndentedStringBuilder) (op:CreateTableOperation) =
        sb
            |> appendEmptyLine
            |> append "type private " |> append op.Name |> appendLine "Table = {"
            |> indent
            |> appendLines (op.Columns |> Seq.map (fun c -> sprintf "%s: OperationBuilder<AddColumnOperation>" c.Name)) false
            |> unindent
            |> appendLine "}"
            |> ignore

    let createTypesForOperations (operations: MigrationOperation seq) (sb: IndentedStringBuilder) =
        operations
            |> Seq.filter(fun op -> (op :? CreateTableOperation))
            |> Seq.map(fun op -> (op :?> CreateTableOperation))
            |> Seq.iter(fun op -> op |> writeCreateTableType sb)
        sb

    member private this.GenerateMigrationImpl (migrationNamespace) (migrationName) (migrationId: string) (contextType:Type) (upOperations) (downOperations) (model) =
        let sb = IndentedStringBuilder()

        let allOperations = (upOperations |> Seq.append downOperations)

        let operationNamespaces = this.GetNamespaces allOperations
        
        let namespaces =
            [ "Microsoft.EntityFrameworkCore"
              "Microsoft.EntityFrameworkCore.Infrastructure"
              "Microsoft.EntityFrameworkCore.Metadata"
              "Microsoft.EntityFrameworkCore.Migrations"
              "Microsoft.EntityFrameworkCore.Storage.ValueConversion" ]
            |> Seq.append operationNamespaces
            |> Seq.append [contextType.Namespace]
            |> Seq.filter (isNull >> not)
            |> Seq.toList
            |> sortNamespaces

        sb
        |> appendAutoGeneratedTag
        |> append "namespace " |> appendLine (code.Namespace [|migrationNamespace|])
        |> appendEmptyLine
        |> writeNamespaces namespaces
        |> appendEmptyLine
        |> createTypesForOperations allOperations // This will eventually become redundant with anon record types
        |> appendEmptyLine
        |> append "[<DbContext(typeof<" |> append (contextType |> code.Reference) |> appendLine ">)>]"
        |> append "[<Migration(" |> append (migrationId |> code.Literal) |> appendLine ")>]"
        |> append "type " |> append (migrationName |> code.Identifier) |> appendLine "() ="
        |> indent |> appendLine "inherit Migration()"
        |> appendEmptyLine
        |> appendLine "override this.Up(migrationBuilder:MigrationBuilder) ="
        |> indent |> ignore

        generator.Generate("migrationBuilder", upOperations, sb)

        sb
        |> appendEmptyLine
        |> unindent |> appendLine "override this.Down(migrationBuilder:MigrationBuilder) ="
        |> indent |> ignore

        let sbLengthBeforeDown = sb.Length

        generator.Generate("migrationBuilder", downOperations, sb)

        // F# requires an explicit close to the function if no down operations are found.
        if sb.Length = sbLengthBeforeDown then
            sb
                |> appendLine "()"
                |> ignore

        sb
        |> unindent
        |> appendEmptyLine
        |> appendLine "override this.BuildTargetModel(modelBuilder: ModelBuilder) ="
        |> indent
        |> ignore

        snapshot.Generate("modelBuilder", model, sb)

        sb
        |> appendEmptyLine
        |> string

    member private this.GenerateSnapshotImpl (modelSnapshotNamespace: string) (contextType: Type) (modelSnapshotName: string) (model: IModel) =
        let sb = IndentedStringBuilder()

        let defaultNamespaces =
            seq {
                 yield "System"
                 yield "Microsoft.EntityFrameworkCore"
                 yield "Microsoft.EntityFrameworkCore.Infrastructure"
                 yield "Microsoft.EntityFrameworkCore.Metadata"
                 yield "Microsoft.EntityFrameworkCore.Migrations"
                 yield "Microsoft.EntityFrameworkCore.Storage.ValueConversion"

                 if contextType.Namespace |> String.IsNullOrEmpty |> not then
                    yield contextType.Namespace
            }
            |> Seq.toList

        let modelNamespaces =
            this.GetNamespaces model
            |> Seq.toList

        let namespaces =
            (defaultNamespaces @ modelNamespaces)
            |> sortNamespaces
            |> Seq.distinct

        sb
            |> appendAutoGeneratedTag
            |> append "namespace " |> appendLine (code.Namespace [|modelSnapshotNamespace|])
            |> appendEmptyLine
            |> writeNamespaces namespaces
            |> appendEmptyLine
            |> append "[<DbContext(typeof<" |> append (contextType |> code.Reference) |> appendLine ">)>]"
            |> append "type " |> append (modelSnapshotName |> code.Identifier) |> appendLine "() ="
            |> indent |> appendLine "inherit ModelSnapshot()"
            |> appendEmptyLine
            |> appendLine "override this.BuildModel(modelBuilder: ModelBuilder) ="
            |> indent
            |> ignore

        snapshot.Generate("modelBuilder", model, sb)

        sb
            |> appendEmptyLine
            |> unindent
            |> string

    override __.Language with get() = "F#"
    override __.FileExtension with get() = ".fs"

    // Defined in the order of when it's called during migration add
    override this.GenerateMigration (migrationNamespace, migrationName, upOperations, downOperations) =
        tempUpOperations <- Seq.toList upOperations
        tempDownOperations <- Seq.toList downOperations
        tempMigrationName <- migrationName
        "// intentionally empty"

    override this.GenerateMetadata (migrationNamespace, contextType, migrationName, migrationId, targetModel) =
        if tempMigrationName = migrationName then
            this.GenerateMigrationImpl migrationNamespace migrationName migrationId contextType tempUpOperations tempDownOperations targetModel
        else
            invalidOp "Migration isn't the same as previously saved during GenerateMigration, DEV: did the order of operations change?"

    override this.GenerateSnapshot (modelSnapshotNamespace, contextType, modelSnapshotName, model) =
        this.GenerateSnapshotImpl modelSnapshotNamespace contextType modelSnapshotName model

