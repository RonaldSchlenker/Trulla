﻿module Trulla.Internal.Typing

open Helper
open Parsing

type TVar = TVar of int | Root

type BindingContext = Map<string, TVar>

type TVal<'a> =
    { range: Range
      tvar: TVar
      bindingContext: BindingContext
      value: 'a }
        override this.ToString() = $"({this.range}){this.value}"

type TExp =
    | Text of string
    | Hole of TVal<MemberExp>
    | For of ident: TVal<string> * exp: TVal<MemberExp> * body: TExp list
    | If of cond: TVal<MemberExp> * body: TExp list
and MemberExp =
    | AccessExp of {| instanceExp: TVal<MemberExp>; memberName: string |}
    | IdentExp of string

module TVar =
    let zero = TVar 0

module TVal =
    let create range tvar bindingContext value =
        { range = range; tvar = tvar; bindingContext = bindingContext; value = value }
    let createZero range value = 
        create range TVar.zero Map.empty value
    let ofPVal tvar bindingContext (pval: PVal<'a>) = 
        create pval.range tvar bindingContext pval.value
    let ofPValZero (pval: PVal<'a>) =
        ofPVal TVar.zero Map.empty pval

module Exp =
    let rec ofPExpZero (pexp: PVal<Parsing.MemberToken>) : TVal<MemberExp> =
        let tval value = TVal.createZero pexp.range value
        match pexp.value with
        | AccessToken accExp ->
            let accExp = {| instanceExp = ofPExpZero accExp.instanceExp; memberName = accExp.memberName  |}
            tval  (AccessExp accExp)
        | IdentToken identExp ->
            tval (IdentExp identExp)

// TODO: meaningful error messages + location
// TODO: Don't throw; return TemplateError results
let buildTree (tokens: PVal<Token> list) : Result<TExp list, TrullaError list> =
    let rec toTree (pointer: int) scopeDepth =
        let mutable pointer = pointer
        let mutable scopeDepth = scopeDepth
        let tree = 
            let mutable endTokenDetected = false
            [ while not endTokenDetected && pointer < tokens.Length do
                let token = tokens[pointer]
                pointer <- pointer + 1
                let descentWithNewScope makeScopeToken =
                    scopeDepth <- scopeDepth + 1
                    let newPointer,children,newScopeDepth = toTree pointer scopeDepth
                    let exp = makeScopeToken children
                    scopeDepth <- newScopeDepth
                    pointer <- newPointer
                    exp
                match token.value with
                | Token.Text x -> Text x
                | Token.Hole x -> Hole (Exp.ofPExpZero x)
                | Token.For (ident, acc) ->
                    descentWithNewScope (fun children -> For (TVal.ofPValZero ident, Exp.ofPExpZero acc, children))
                | Token.If acc ->
                    descentWithNewScope (fun children -> If (Exp.ofPExpZero acc, children))
                | Token.End ->
                    match scopeDepth with
                    | 0 ->
                        { ranges = [token.range]
                          message = "Closing a scope is not possible without having a scope open." }
                        |> TrullaException
                        |> raise
                    | n -> scopeDepth <- n-1
            ]
        pointer,tree,scopeDepth
    try 
        let _,tree,scopeDepth = toTree 0 0
        if scopeDepth > 0 then
            // TODO: Range.zero is wrong
            { ranges = [Range.zero]; message ="TODO: Unclosed scope detected." }
            |> List.singleton
            |> Error
        else
            Ok tree
    with TrullaException err ->
        Error [err] 

type Type =
    | Mono of string
    | Poly of name: string * typParam: Type
    | RecDef of Field list
    | RecRef of TVar
    | Var of TVar // TODO: why VAR is in here?
    override this.ToString() =
        match this with
        | Mono s -> s
        | Poly (n,tp) -> $"%O{n}<%O{tp}>"
        | RecDef fields ->
            let renderField (fn,ft) = $"""{fn}: %O{ft}"""
            let fields = fields |> List.map renderField |> String.concat "; "
            $"""{{ {fields} }}"""
        | Var tvar -> string tvar
        | RecRef tvar -> $"""(RecRef {match tvar with Root -> "ROOT" | TVar tvar -> $"{tvar}"})"""
and Field = string * Type

// TODO: Problem should be TVar * Type; later, resolve the ranges
type Problem = Problem of TVar * TVal<Type>

module KnownTypes =
    // TODO: reserve these keywords + parser tests
    let [<Literal>] string = "string"
    let [<Literal>] bool = "bool"
    let [<Literal>] sequence = "sequence"
    let sequenceOf elemTypId = sequence, elemTypId

type TVarGen() =
    let mutable x = -1
    member this.Next() = x <- x + 1; TVar x

// TODO: Prevent shadowing
let buildProblems (tree: TExp list) =
    let tvargen = TVarGen()
    
    let rec constrainMemberExp (bindingContext: BindingContext) (membExp: TVal<MemberExp>) =
        match membExp.value with
        | AccessExp accExp ->
            let tvarInstance,instanceProblems = constrainMemberExp bindingContext accExp.instanceExp
            let tvarExp = tvargen.Next()
            let problems = [
                yield! instanceProblems
                yield Problem (tvarInstance, TVal.create membExp.range tvarExp bindingContext (RecDef [accExp.memberName, Var tvarExp]))
            ]
            tvarExp,problems
        | IdentExp ident ->
            let tvarIdent = bindingContext |> Map.tryFind ident
            match tvarIdent with
            | Some tvarIdent ->
                tvarIdent,[]
            | None ->
                let tvarIdent = tvargen.Next()
                let problems = [
                    yield Problem (Root, TVal.create membExp.range tvarIdent bindingContext (RecDef [ident, Var tvarIdent]))
                ]
                tvarIdent,problems
    
    let rec constrainTree (bindingContext: BindingContext) (tree: TExp list) =
        [ for tree in tree do
            match tree with
            | Text _ -> ()
            | Hole hole ->
                let tvarHole,holeProblems = constrainMemberExp bindingContext hole
                yield! holeProblems
                yield Problem (tvarHole, TVal.create hole.range tvarHole bindingContext (Mono KnownTypes.string))
            | For (ident,source,children) ->
                let tvarIdent = tvargen.Next()
                let innerBindingContext = bindingContext |> Map.add ident.value tvarIdent
                let tvarSource,sourceProblems = constrainMemberExp innerBindingContext source
                yield! sourceProblems
                yield Problem (tvarSource, TVal.create source.range tvarIdent bindingContext (Poly (KnownTypes.sequenceOf (Var tvarIdent))))
                // --->
                yield! constrainTree innerBindingContext children
            | If (cond,children) ->
                let tvarCond,condProblems = constrainMemberExp bindingContext cond
                yield! condProblems
                yield Problem (tvarCond, TVal.create cond.range tvarCond bindingContext (Mono KnownTypes.bool))
                // --->
                yield! constrainTree bindingContext children
        ]
    constrainTree Map.empty tree

let rec subst tvarToReplace withTyp inTyp =
    let withTyp = match withTyp with RecDef _ -> RecRef tvarToReplace | _ -> withTyp
    match inTyp with
    | Poly (name, inTyp) -> Poly (name, subst tvarToReplace withTyp inTyp)
    | RecDef fields -> RecDef [ for fn,ft in fields do fn, subst tvarToReplace withTyp ft ]
    | Var tvar when tvar = tvarToReplace -> withTyp
    | _ -> inTyp

let rec unify originalTvar t1 t2 =
    [
        printfn $"Unifying: ({t1.value})  --  ({t2.value})"

        // TODO: Correct range mapping when constructing new problems
        match t1.value, t2.value with
        | t1,t2 when t1 = t2 -> ()
        | Var _, Var tvar ->
            printfn "YIELD a"
            yield Problem (tvar, t1)
        | Var tvar, t
        | t, Var tvar ->
            printfn "YIELD b"
            yield Problem (tvar, {t1 with value = t})
        | Poly (n1,pt1), Poly (n2,pt2) when n1 = n2 ->
            printfn "YIELD c ->"
            yield! unify originalTvar {t1 with value = pt1} {t2 with value = pt2}
        | RecRef tvarRec, (RecDef _ as r)
        | (RecDef _ as r), RecRef tvarRec ->
            printfn "YIELD d"
            yield Problem (tvarRec, {t1 with value = r})
        | RecDef f1, RecDef f2 ->
            let unifiedFieldsAndProblems =
                (f1 @ f2)
                |> List.groupBy fst
                |> List.map (fun (fname, ftypes) ->
                    // We finally use only ft1. This works because 'unify' only needs to emit
                    // new problems, but it doesn't resolve 2 types to one by a type hierarchy or
                    // something like that; out type system only has "equal" or "unequal" types.
                    let ftype,problems =
                        ftypes
                        |> List.map (fun (_,ft) -> ft,[])
                        |> List.reduce (fun (ft1,problems) (ft2,_) ->
                            ft1, problems @ unify originalTvar {t1 with value = ft1} {t2 with value = ft2}
                        )
                    (fname,ftype),problems
                )
            let fields = unifiedFieldsAndProblems |> List.map fst
            let res = [
                yield Problem (originalTvar, TVal.create t1.range t1.tvar t1.bindingContext (RecDef fields))
                yield! unifiedFieldsAndProblems |> List.collect snd
                ]
            printfn $"Resulting RecordProblems: %A{res}"
            yield! res
        | _ ->
            { ranges = [t1.range; t2.range]
              message = $"TODO: Can't unitfy types {t1.value} and {t2.value}" }
            |> TrullaException
            |> raise
    ]
    
let substMany tvarToReplace withType inProblems =
    [ for (Problem (ptvar, ptype)) in inProblems do
        let ptype = { ptype with value = subst tvarToReplace withType.value ptype.value }
        match ptvar = tvarToReplace with
        | false -> yield Problem (ptvar, ptype)
        | true -> yield! unify ptvar withType ptype
    ]

let solveProblems (problems: Problem list) =
    let rec solve problems solution =
        match problems with
        | [] -> solution
        | (Problem (tvar, typ) as p) :: ps ->
            let problems = substMany tvar typ ps
            let solution = p :: substMany tvar typ solution
            solve problems solution
    try Ok (solve problems [])
    with TrullaException err -> Error [err]

type UnificationResult =
    { tvar: TVar
      errors: string list
      resultingTyp: Type }

let buildRecords (problems: Problem list) =
    problems
    |> List.map (fun (Problem (tvar,t)) -> tvar,t)
    |> List.choose (fun (tvar,t) -> match t.value with RecDef f -> Some (tvar,f) | _ -> None)
    |> List.groupBy fst
    |> List.map (fun (tvar, fields) ->
        // comment why 'distinct' is needed (merging fields: original problem always remains in solution ist)
        tvar, fields |> List.collect snd |> List.distinct)
    |> Map.ofList

let solve tokens =
    result {
        let! tokens = tokens 
        let! tree = buildTree tokens
        let problems = buildProblems tree
        let! solution = solveProblems problems
        let records = buildRecords solution
        return tree,records
    }
    