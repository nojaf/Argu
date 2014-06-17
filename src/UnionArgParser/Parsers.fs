﻿namespace Nessos.UnionArgParser

    module internal Parsers =
        
        open System
        open System.IO
        open System.Configuration

        open Nessos.UnionArgParser.Utils
        open Nessos.UnionArgParser.ArgInfo

        // parse the next command line argument and append to state
        let parseCommandLinePartial (argIdx : Map<string, ArgInfo>) (args : string []) (pos : int ref)
                                    (isHelpRequested : bool, parseState : Map<ArgId, ParseResult<'Template> list>) =

            let curr = args.[!pos]
            do incr pos
            
            if hasCommandLineParam helpInfo curr then (true, parseState) else

            match argIdx.TryFind curr with
            | None -> bad ErrorCode.CommandLine None "unrecognized argument: '%s'." curr
            | Some argInfo when argInfo.IsFirst && !pos > 1 ->
                bad ErrorCode.CommandLine (Some argInfo) "argument '%s' should be first." curr
            | Some argInfo ->
                let parseOne () =
                    let fields =
                        [|
                            for p in argInfo.FieldParsers do
                                if !pos = args.Length then
                                    bad ErrorCode.CommandLine (Some argInfo) 
                                        "option '%s' requires argument <%O>." curr p
                                yield 
                                    try p.Parser args.[!pos]
                                    with _ ->
                                        if argInfo.PrintLabels then
                                            bad ErrorCode.CommandLine (Some argInfo) 
                                                "option '%s' expects argument <%O>." curr p
                                        else
                                            bad ErrorCode.CommandLine (Some argInfo) 
                                                "option '%s' expects argument <%O>." curr p
                                incr pos
                        |]

                    buildResult<'Template> argInfo CommandLine curr fields

                let parsedResults =
                    if argInfo.IsRest then
                        [
                            while !pos < args.Length do
                                yield parseOne ()
                        ]
                    else [ parseOne () ]

                let previous = defaultArg (parseState.TryFind argInfo.Id) []
                
                isHelpRequested, parseState.Add(argInfo.Id, previous @ parsedResults)
                

        // parse the entire command line
        let parseCommandLine argIdx (inputs : string []) =
            let state = ref (false, Map.empty)
            let pos = ref 0

            while !pos < inputs.Length do
                state := parseCommandLinePartial argIdx inputs pos !state

            !state

        // AppSettings parse errors are threaded to the state rather than raised directly;
        // this happens since AppSettings errors are overriden by default in case of a valid command line input.
        let parseAppSettingsPartial (appSettingsReader : string -> string)
                                    (state : Map<ArgId, Choice<ParseResult<'Template> list, exn>>) (aI : ArgInfo) =

            try
                match aI.AppSettingsName with
                | None -> state
                | Some name ->
                    let parseResults =
                        match appSettingsReader name with
                        | null | "" -> []
                        | entry when aI.FieldParsers.Length = 0 ->
                            match Boolean.tryParse entry with
                            | None -> bad ErrorCode.AppSettings (Some aI) "AppSettings entry '%s' is not <bool>." name
                            | Some flag when flag -> [buildResult aI CommandLine name [||]]
                            | Some _ -> []
                        | entry ->
                            let tokens = 
                                if aI.AppSettingsCSV || aI.FieldParsers.Length > 1 then entry.Split(',') 
                                else [| entry |]

                            let pos = ref 0

                            let readNext() =
                                let fields =
                                    [|
                                        for p in aI.FieldParsers do
                                            if !pos < tokens.Length then
                                                yield 
                                                    try p.Parser <| tokens.[!pos]
                                                    with _ -> 
                                                        bad ErrorCode.AppSettings (Some aI) 
                                                            "AppSettings entry '%s' is not <%O>." name p

                                                incr pos
                                            else
                                                bad ErrorCode.AppSettings (Some aI) 
                                                    "AppSettings entry '%s' missing <%O> argument." name p
                                    |]

                                buildResult aI AppSettings name fields

                            if aI.AppSettingsCSV then
                                [
                                    while !pos < tokens.Length do
                                        yield readNext ()
                                ]
                            else [ readNext () ]

                    state.Add(aI.Id, Choice1Of2 parseResults)

            with Bad _ as e -> state.Add(aI.Id, Choice2Of2 e)

        let parseAppSettings appConfigFile (argInfo : ArgInfo list) =
            let appSettingsReader : string -> string =
                match appConfigFile with
                | None -> fun name -> ConfigurationManager.AppSettings.[name]
                | Some file when File.Exists file ->
                    let fileMap = new ExeConfigurationFileMap()
                    fileMap.ExeConfigFilename <- file
                    let config = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None)

                    fun name ->
                        match config.AppSettings.Settings.[name] with
                        | null -> null
                        | entry -> entry.Value

                // file not found, return null strings for everything
                | Some _ -> fun _ -> null

            List.fold (parseAppSettingsPartial appSettingsReader) Map.empty argInfo

        // does what the type signature says; combines two parse states into one according to the provided rules.
        let combine (argInfo : ArgInfo list) ignoreMissing 
                        (appSettingsResults : Map<ArgId, Choice<ParseResult<'Template> list, exn>> option)
                        (commandLineResults : Map<ArgId, ParseResult<'Template> list> option) =

            let argInfo, appSettingsResults, commandLineResults =
                match appSettingsResults, commandLineResults with
                | None, None -> failwith "need at least one input source."
                | Some m, None -> List.filter isAppConfig argInfo, m, Map.empty
                | None, Some m -> List.filter isCommandLine argInfo, Map.empty, m
                | Some m, Some m' -> argInfo, m, m'

            let combineSingle (argInfo : ArgInfo) =
                let acr = defaultArg (appSettingsResults.TryFind argInfo.Id) <| Choice1Of2 []
                let clr = defaultArg (commandLineResults.TryFind argInfo.Id) []

                let combined =
                    match acr, clr with
                    | Choice1Of2 ts, [] -> ts
                    | Choice2Of2 e, [] -> raise e
                    | Choice2Of2 e, _ when argInfo.GatherAllSources -> raise e
                    | Choice1Of2 ts, ts' when argInfo.GatherAllSources -> ts @ ts'
                    | _, ts' -> ts'

                match combined with
                | [] when argInfo.Mandatory && not ignoreMissing -> 
                    bad ErrorCode.PostProcess (Some argInfo) "missing parameter '%s'." <| getName argInfo
                | _ -> combined

            argInfo |> Seq.map (fun aI -> aI.Id, (aI, combineSingle aI)) |> Map.ofSeq