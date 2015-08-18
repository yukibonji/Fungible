﻿module Fungible.Simple

open Fungible.Core
open Fungible.Attributes

open System
open System.Collections.Generic
open System.Linq
open System.Linq.Expressions
open System.ComponentModel

open FSharp
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection

[<CLIMutable>]
type FunctionCleanerDefinition = 
    {
        [<Description("The target field for this cleaner")>]
        Field: string
        [<Description("The operation to perform on the target field")>]
        Operation: string
        [<Description("Arguments to be passed to the operation")>]
        Args: string []
    }
    with override t.ToString () = sprintf "%s <- %s with %A" t.Field t.Operation t.Args

module Types = 

    type TypeKind =
        | Inner
        | Outter
        | Fixed of Type

    type FunctionKind =
        {
            ExprWrapper: Expr -> FieldAction
            InputKind: TypeKind
            OutputKind: TypeKind
        }

    let getFunctionKind (funcType: string) = 
        match funcType.ToLowerInvariant() with
        | "map" ->      Map,        Inner,                  Inner
        | "filter" ->   Filter,     Inner,                  Fixed typeof<bool>
        | "collect" ->  Collect,    Inner,                  Outter
        | "default" ->  Default,    Fixed typeof<unit>,     Outter
        | "function" -> Function,   Outter,                 Outter
        | "add" ->      Add,        Fixed typeof<unit>,     Outter
        | otherwise -> failwithf "Unexpected type in data cleaner: %s" otherwise     
        |> fun (ew, ik, ok) -> { ExprWrapper = ew; InputKind = ik; OutputKind = ok }

    let getInnerType (targetType: Type) = 
        match targetType with
        | IsMapType t -> FSharpType.MakeTupleType (t.GetGenericArguments())
        | IsOptionType t -> t.GetGenericArguments().[0] 
        | t when t.IsArray -> t.GetElementType()
        | t -> t    

    let getActualType (targetType: Type) (tk: TypeKind) =
        match tk with
        | Inner -> getInnerType targetType 
        | Outter -> targetType
        | Fixed t -> t

    let nameToPath (name: string) = 
        name.Split([|'.'|], StringSplitOptions.None) |> Array.toList |> List.rev

module internal Internals = 
    open Types

    let convertFromArgsToInputType (t: Type) (args: string []) =
        match t with
        | t when t = typeof<string[]> -> args |> box
        | t when t = typeof<char[]> -> args |> Array.map (fun arg -> char arg) |> box
        | _ -> failwithf "Unable to convert to basic cleaner input type: %s" (t.FullName)

    let generateBasicCleaner (fmod: Type) (targetType: Type) (funcName: string) (funcArg: string []) =
        let mi = fmod.GetMethod(funcName)
        let prms = mi.GetParameters()

        if prms.Length > 2 then
            failwithf "Error while resolving basic cleaner function: %s, too many parameters found." funcName

        if prms.Length = 1 && funcArg.Length > 0 then
            failwithf "Basic data cleaning function %s does not support arguments." funcName 

        let funcType = 
            match getFunctionType(mi) with
            | Some v -> v
            | None -> failwithf "Basic data cleaning not supported with %s" funcName 

        let functionKind = getFunctionKind funcType.Type

        let inType = getActualType targetType functionKind.InputKind
        //let outType = getActualType targetType functionKind.OutputKind

        if prms.Length = 1 then
            let arg = Var("x", inType, false)
            let useArg = Expr.Var(arg)

            let funcExpr = Expr.Call(mi, [useArg])
            Expr.Lambda(arg, funcExpr) |> functionKind.ExprWrapper
        else
            let arg = Var("x", inType, false)
            let useArg = Expr.Var(arg)
            let dcFuncArgs =  
                try 
                    let argsPrm = mi.GetParameters() |> Array.map (fun pi -> pi.ParameterType)
                    if argsPrm.Length <> 2 then failwith "Function has an incorrect number of parameters." funcName
                    let convertedArgs = convertFromArgsToInputType argsPrm.[0] funcArg
                    Expr.Coerce(<@@ convertedArgs @@>, argsPrm.[0])
                with ex -> failwithf "An error occured while creating a basic cleaner with function (%s) and arguments (%A): %s" funcName funcArg ex.Message

            let funcExpr = Expr.Call(mi, [ dcFuncArgs; useArg ])
            Expr.Lambda(arg, funcExpr) |> functionKind.ExprWrapper

open Internals
open Types

type Cleaner = string list * FieldAction

let generateBasicCleaner<'U> (functionModule: Type) (propertyMap: Map<string list, Type>) (basic: FunctionCleanerDefinition) : Cleaner =
    let path = nameToPath basic.Field 
    let propertType = propertyMap.[path]
    let cleaner = generateBasicCleaner functionModule propertType basic.Operation basic.Args
    path, cleaner

let compileCleaners<'U, 'T> (cleaners: Cleaner seq) =
    let recordCloningSettings = { CloneWhenNoChanges = false; FailOnUnsupportedType = true }

    let compiledCleaners = 
        cleaners 
        |> Seq.groupBy fst
        |> Seq.map (fun (sl, slfa) -> sl, slfa |> Seq.map snd |> Seq.toList)
        |> Map.ofSeq    
    genrateRecordDeepCopyFunctionWithArgs<'U,'T> recordCloningSettings "replaceMe" compiledCleaners
