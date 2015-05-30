#load "AutoCancelAgent.fs"
#load "ViewModelAgent.fs"
open ViewModelAgent


type ViewPage = 
    | Registration of string * string * string
    | Login of string * string
type PrintThis = |Print

type VMCache<'S>() = 
    let x = ref (Map.empty: Map<string,'S>)
    interface IViewModelCache<'S> with
        member this.GetState(key,defaultState) = async {
            let result = 
                match x.Value.TryFind(key) with
                | Some(y) -> y
                | None -> defaultState
            return result }
        member this.SetState(key,state) = async {
            x := x.Value.Add(key,state)
            return () }
        member this.FlushState(key) = async {
            x := x.Value.Remove(key)
            return ()}

type VMLogger<'S,'A>() =
    interface IViewModelLogger<'S,'A> with 
        member this.LogTransition(state,newState) = printfn "Transitioned from %A to %A" state newState
        member this.LogAction(action) = printfn "Performed %A" action
        member this.LogState(state) = printfn "Currently in %A" state
        member this.LogLifecycleTransition(state,newState) = printfn "Lifecycle change from %A to %A" state newState
        member this.LogObserverSubscription(obs) = printfn "New Observer: %A" obs
        member this.LogObserverCompletion(obs) = printfn "Observer %A has completed" obs
        member this.LogSupervisorAction(ex) = printfn "Supervisor handling: %A" ex
        member this.LogPublishAction(ex) = printfn "Published: %A" ex

let vmName = "ThisViewModel"
let initState = Login("","")
let actionHandler (state,action) = 
    printfn "STATE: %A, ACTION: %A" state action
    state
let vmCache = VMCache<ViewPage>()
let vmLogger = VMLogger<ViewPage,PrintThis>()
let exnHandler = function
    | UnhandledViewModelEvent(s,e) -> ()
    | _ -> ()

let login = ViewModelAgent(vmName,initState,actionHandler,vmCache,vmLogger,exnHandler)

let o1 = login.AsObservable
let o2 = login.AsObservable

let renderer1 s = printfn "RENDERING %A in format1" s
let renderer2 s = printfn "RENDERING %A in format2" s
o1.Add(renderer1)
o1.Add(renderer2)
login.Start()
login.Post(Print)
login.Kill()