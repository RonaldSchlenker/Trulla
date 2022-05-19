﻿module Trulla.Typing

open Parsing

type Tree =
    | LeafNode of LeafToken
    | InternalNode of root: ScopeToken * children: Tree list
and LeafToken =
    | Text of string
    | Hole of PVal<Exp>
and ScopeToken =
    | For of ident: PVal<string> * exp: PVal<Exp>
    | If of PVal<Exp>

// TODO: meaningful error messages + location
// TODO: Don't throw; return TemplateError results
let buildTree (tokens: ParseResult) : Tree list =
    let mutable scopeStack = []
    let rec toTree (pointer: int) =
        let mutable pointer = pointer
        let nodes = 
            let mutable endTokenDetected = false
            [ while not endTokenDetected && pointer < tokens.Length do
                let token = tokens[pointer]
                pointer <- pointer + 1
                let descentWithNewScope scopeToken =
                    scopeStack <- scopeToken :: scopeStack
                    let newPointer,children = toTree pointer
                    let res = InternalNode (scopeToken, children)
                    pointer <- newPointer
                    res
                match token with
                | ParserToken.Text x -> yield LeafNode (Text x)
                | ParserToken.Hole x -> yield LeafNode (Hole x)
                | ParserToken.For (ident, acc) -> yield descentWithNewScope (For (ident, acc))
                | ParserToken.If acc -> yield descentWithNewScope (If acc)
                | ParserToken.End ->
                    match scopeStack with
                    | [] -> failwith "TODO: Closing an unopened scope"
                    | _ :: xs -> scopeStack <- xs
            ]
        pointer,nodes
    let tree = snd (toTree 0)
    if scopeStack.Length > 0
        then failwith "TODO: Unclosed scope detected."
        else tree

type TVar =
    | TVar of int
    | Root

type Type =
    | Mono of string
    | Poly of name:string * typParam:Type
    | Var of TVar // TODO: why VAR is in here?
and Field = string * Type

type Constraint =
    | IsType of Type
    | HasField of Field

type BindingContext = Map<string, TVar>
type Problem = Problem of TVar * Constraint

module KnownTypes =
    // TODO: reserve these keywords + parser tests
    let string = "string"
    let bool = "bool"
    let sequence elemTypId = "sequence", elemTypId

// TODO: Prevent shadowing
let collectConstraints (trees: Tree list) =
    let newTVar =
        let mutable x = -1
        fun () ->
            x <- x + 1
            TVar x
    
    let rec constrainExp (bindingContext: BindingContext) exp =
        match exp with
        | AccessExp exp ->
            let tvarInstance,instanceProblems = constrainExp bindingContext exp.instanceExp
            let tvarExp = newTVar()
            let problems = [
                yield! instanceProblems
                ////Problem (tvarInstance, IsRecordDefinition)
                Problem (tvarInstance, HasField (exp.memberName, Var tvarExp))
            ]
            tvarExp,problems
        | IdentExp ident ->
            let tvarIdent = bindingContext |> Map.tryFind ident
            match tvarIdent with
            | Some tvarIdent ->
                tvarIdent,[]
            | None ->
                let tvarIdent = newTVar()
                let problems = [
                    Problem (Root, HasField (ident, Var tvarIdent))
                    // tvarRoot is also a record; but we can omit this
                ]
                tvarIdent,problems
    
    let rec buildConstraints (trees: Tree list) (bindingContext: BindingContext) =
        [ for tree in trees do
            match tree with
            | LeafNode (Text _) ->
                ()
            | LeafNode (Hole hole) ->
                let tvarHole,holeProblems = constrainExp bindingContext hole.value
                yield! holeProblems
                yield Problem (tvarHole, IsType (Mono KnownTypes.string))
            | InternalNode (For (ident,source), children) ->
                let tvarIdent = newTVar()
                let bindingContext = bindingContext |> Map.add ident.value tvarIdent
                let tvarSource,sourceProblems = constrainExp bindingContext source.value
                yield! sourceProblems
                yield Problem (tvarSource, IsType (Poly (KnownTypes.sequence (Var tvarIdent))))
                // --->
                yield! buildConstraints children bindingContext
            | InternalNode (If cond, children) ->
                let tvarCond,condProblems = constrainExp bindingContext cond.value
                yield! condProblems
                yield Problem (tvarCond, IsType (Mono KnownTypes.bool))
                // --->
                yield! buildConstraints children bindingContext
        ]
    
    // TODO: return also tvarRoot
    buildConstraints trees Map.empty

type FinalType =
    | Type of Type
    | Record of Record
and Record = { name: string; fields: Field list }

// TODO: Ranges wieder überall reinmachen
////type TemplateError = { message: string; range: Range }
type UnificationResult =
    { tvar: TVar
      errors: string list
      resultingTyp: FinalType }

let solveProblems (problems: Problem list) =
    
    let rec substTyp tvarToReplace by typ =
        match typ with
        | Poly (name, typ) -> Poly (name, substTyp tvarToReplace by typ)
        | Var tvar when tvar = tvarToReplace -> by
        | _ -> typ

    let substConstraint tvarToReplace by constr =
        match constr with
        | IsType typ -> IsType (substTyp tvarToReplace by typ)
        | HasField (name,typ) -> HasField (name, substTyp tvarToReplace by typ)

    let rec unifyTypes t1 t2 =
        [ 
            match t1,t2 with
            | t1,t2 when t1 = t2 ->
                ()
            | Var tvar, Var _ -> // TODO: Kann das sein?
                yield Problem (tvar, IsType t1)
            | Var tvar, (Poly _ as t)
            | (Poly _ as t), Var tvar ->
                yield Problem (tvar, IsType t)
            | Poly (n1,t1), Poly (n2,t2) when n1 = n2 ->
                yield! unifyTypes t1 t2
            | _ ->
                failwith $"TODO: Can't unitfy {t1} and {t2}"
        ]

    let subst tvarToReplace by problems =
        [ for (Problem (tvar, c)) in problems do
            let c = substConstraint tvarToReplace by c
            match tvar = tvarToReplace with
            | false ->
                yield Problem (tvar, substConstraint tvarToReplace by c)
            | true ->
                // unification Problem
                match c with
                | IsType t ->
                    yield! unifyTypes by t
                | HasField (fname,ftyp) ->
                    match by with
                    | Var tvar ->
                        let ftyp = substTyp tvarToReplace by ftyp
                        yield Problem (tvar, HasField (fname,ftyp))
                    | _ ->
                        failwith $"TODO: Can't unitfy {by} and {c}"
        ]

    let mutable problems : Problem list = problems
    let mutable solution : Problem list = []
    let rec solve () =
        match problems with 
        | [] -> ()
        | (Problem (tvar, c) as p) :: ps ->
            match c with
            | IsType t ->
                problems <- subst tvar t ps
                solution <- subst tvar t (p::solution)
            | HasField _ ->
                problems <- ps
                solution <- p::solution
            do solve()
    do solve()

    solution
