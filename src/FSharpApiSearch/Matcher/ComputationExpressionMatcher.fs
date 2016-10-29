﻿module internal FSharpApiSearch.ComputationExpressionMatcher

open MatcherTypes
open FSharp.Collections.ParallelSeq

module Filter =
  let instance (_: SearchOptions) =
    { new IApiMatcher with
        member this.Name = "Computation Expression Filter"
        member this.Test lowTypeMatcher query api ctx =
          match api.Kind with
          | ApiKind.ComputationExpressionBuilder -> Failure
          | _ -> Matched ctx }

let private map (options: SearchOptions) f (xs: #seq<_>) =
  match options.Parallel with
  | Enabled -> PSeq.map f xs :> seq<_>
  | Disabled -> Seq.map f xs

let private filter (options: SearchOptions) f (xs: #seq<_>) =
  match options.Parallel with
  | Enabled -> PSeq.filter f xs :> seq<_>
  | Disabled -> Seq.filter f xs

let private collect (options: SearchOptions) f (xs: #seq<_>) =
  match options.Parallel with
  | Enabled -> PSeq.collect f xs :> seq<_>
  | Disabled -> Seq.collect f xs

let private choose (options: SearchOptions) f xs =
  match options.Parallel with
  | Enabled -> PSeq.choose f xs :> seq<_>
  | Disabled -> Seq.choose f xs

let private append options xs ys =
  match options.Parallel with
  | Enabled -> PSeq.append xs ys :> seq<_>
  | Disabled -> Seq.append xs ys

let test (lowTypeMatcher: ILowTypeMatcher) (builderTypes: LowType) (ctx: Context) (api: Api) =
  match api.Signature with
  | ApiSignature.ModuleValue (TypeAbbreviation { Original = Arrow xs }) -> lowTypeMatcher.Test builderTypes (List.last xs) ctx
  | ApiSignature.ModuleValue value -> lowTypeMatcher.Test builderTypes value ctx
  | ApiSignature.ModuleFunction xs -> lowTypeMatcher.Test builderTypes ((xs |> List.last |> List.last).Type) ctx
  | _ -> Failure

let testComputationExpressionTypes (lowTypeMatcher: ILowTypeMatcher) ctx queryCeType ceTypes =
  ceTypes |> Seq.exists (fun t -> lowTypeMatcher.Test t queryCeType ctx |> MatchingResult.toBool)

let search (options: SearchOptions) (targets: ApiDictionary seq) (lowTypeMatcher: ILowTypeMatcher) (query: ComputationExpressionQuery) (initialContext: Context) =
  let querySyntaxes = Set.ofList query.Syntaxes

  let testSyntaxes =
    if query.Syntaxes.IsEmpty then
      fun syntaxes -> Set.isEmpty syntaxes = false
    else
      fun syntaxes -> Set.intersect syntaxes querySyntaxes = querySyntaxes

  let builderTypes =
    targets
    |> collect options (fun target ->
      target.Api
      |> choose options (fun api ->
        match api.Signature with
        | ApiSignature.ComputationExpressionBuilder builder ->
          Some (api, builder)
        | _ -> None
      )
      |> filter options (fun (_, builder) -> testComputationExpressionTypes lowTypeMatcher initialContext query.Type builder.ComputationExpressionTypes)
      |> filter options (fun (_, builder) -> testSyntaxes (Set.ofList builder.Syntaxes))
      |> map options (fun (api, builder) ->
        let result = { Distance = 0; Api = api; AssemblyName = target.AssemblyName }
        (result, builder.BuilderType)
      )
    )
    |> Seq.toList

  let builderResults = builderTypes |> Seq.map fst

  let builderTypes = Choice (builderTypes |> List.map snd)

  let apiResults =
    seq {
      for dic in targets do
        for api in dic.Api do
          yield (dic, api)
    }
    |> choose options (fun (dic, api) ->
      match test lowTypeMatcher builderTypes initialContext api with
      | Matched ctx -> Some { Distance = ctx.Distance; Api = api; AssemblyName = dic.AssemblyName }
      | _ -> None
    )

  append options builderResults apiResults