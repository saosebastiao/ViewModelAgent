namespace ViewModelAgent
open System

type Agent<'T> = MailboxProcessor<'T>
type VMState<'S> = 
    | Active of 'S
    | Inactive of 'S
    | Suspended of cacheKey: string 
    | Dead
type VMAction<'A> =
    | Resume //from Suspended or Inactive
    | Suspend // persist VMState in cache 
    | Deactivate // persist VMState in memory
    | Kill // discard all state
    | Action of 'A //normal action

exception UnhandledViewModelEvent of curState:string * event:string

type IViewModelCache<'S> =
    abstract member GetState : string * 'S -> Async<'S>
    abstract member SetState : string * 'S -> Async<unit>
    abstract member FlushState: string -> Async<unit>

type IViewModelLogger<'S,'A>() =
    abstract member LogTransition: 'S * 'S -> unit
    abstract member LogAction: 'A -> unit
    abstract member LogState: 'S -> unit
    abstract member LogLifecycleTransition: VMState<'S> * VMState<'S> -> unit
    abstract member LogObserverSubscription: IObserver<'S> -> unit
    abstract member LogObserverCompletion: IObserver<'S> -> unit
    abstract member LogSupervisorAction: exn -> unit
    abstract member LogPublishAction: exn -> unit
    default this.LogTransition(_,_) = ()
    default this.LogAction(_) = ()
    default this.LogState(_) = ()
    default this.LogLifecycleTransition(_,_) = ()
    default this.LogObserverSubscription(_) = ()
    default this.LogObserverCompletion(_) = ()
    default this.LogSupervisorAction(_) = ()
    default this.LogPublishAction(_) = ()

type ViewModelAgent<'S,'A when 'S: equality>(vmName:string, 
                                             initState: 'S, 
                                             actionHandler: 'S * 'A -> 'S, 
                                             vmCache: IViewModelCache<'S>,
                                             logger: IViewModelLogger<'S,'A>,
                                             exnHandler: exn -> unit) =
    let finished = ref false
    let subscribers = ref (Map.empty : Map<int, IObserver<'S>>)
    let subscriberIDsequence = ref 0
    let publish msg = 
        !subscribers 
        |> Seq.iter (fun (KeyValue(_, sub)) ->
            try sub.OnNext(msg) with ex -> logger.LogPublishAction(ex))
    let completed() = 
        lock subscribers (fun () ->
            finished := true
            !subscribers |> Seq.iter (fun (KeyValue(_, sub)) -> sub.OnCompleted())
            subscribers := Map.empty)
    let supervisor f x = async {
        while true do
            try
                do! f x
            with ex -> 
                logger.LogSupervisorAction(ex) 
                exnHandler(ex) }
    let processor (inbox: MailboxProcessor<VMAction<_>>) init =
        let rec controller = function
        | Active(state) -> async {
            do publish state
            let! msg = inbox.Receive()
            match msg with 
            | Deactivate -> 
                do logger.LogLifecycleTransition(Active(state),Inactive(state))
                return! controller(Inactive(state))
            | Suspend ->
                do! vmCache.SetState(vmName,state) //save to cache!!
                do logger.LogLifecycleTransition(Active(state),Suspended(vmName))
                return! controller(Suspended(vmName))
            | Kill ->
                do! vmCache.FlushState(vmName) //flush cache
                do logger.LogLifecycleTransition(Active(state),Dead)
                return! controller(Dead)
            | Action(action) ->
                do logger.LogAction(action)
                let newState = actionHandler(state,action)
                do logger.LogTransition(state,newState)
                do logger.LogState(newState)
                return! controller(Active(newState))
            | _ -> return! controller(Active(state))
            }
        | Inactive(state) -> async {
            let! msg = inbox.Receive()
            match msg with 
            | Suspend ->
                do! vmCache.SetState(vmName,state) //save to cache!!
                do logger.LogLifecycleTransition(Inactive(state),Suspended(vmName))
                return! controller(Suspended(vmName))
            | Kill ->
                do! vmCache.FlushState(vmName) //flush cache
                do logger.LogLifecycleTransition(Inactive(state),Dead)
                return! controller(Dead)
            | Resume ->
                do logger.LogLifecycleTransition(Inactive(state),Active(state))
                return! controller(Active(state))
            | _ -> return! controller(Inactive(state))
            }
        | Suspended(key) -> async {
            let! msg = inbox.Receive()
            match msg with 
            | Resume ->
                let! resumeState = vmCache.GetState(key,initState)
                do logger.LogLifecycleTransition(Suspended(key),Active(resumeState))
                return! controller(Active(resumeState))
            | Kill ->
                do! vmCache.FlushState(vmName) //flush cache
                do logger.LogLifecycleTransition(Suspended(key),Dead)
                return! controller(Dead)
            | _ -> return! controller(Suspended(key))
            }
        | Dead -> async {
            let! msg = inbox.Receive()
            match msg with 
            | Resume -> 
                do logger.LogLifecycleTransition(Dead,Active(initState))
                return! controller(Active(initState))
            | Kill -> 
                return! controller(Dead)
            | _ -> return! controller(Dead)
        }
        controller(Dead)
    let vm = Agent<VMAction<_>>.Start(fun inbox -> supervisor (processor inbox) initState)
    let obs = 
        { new IObservable<'S> with 
            member this.Subscribe(obs) =
                let subscriberKey =
                    lock subscribers (fun () ->
                        if !finished then failwith "ViewModelAgent has already completed"
                        let key = !subscriberIDsequence
                        subscriberIDsequence := !subscriberIDsequence + 1
                        subscribers := subscribers.Value.Add(key, obs)
                        key)
                do logger.LogObserverSubscription(obs)
                { new IDisposable with  
                    member this.Dispose() = 
                        lock subscribers (fun () -> 
                            subscribers := subscribers.Value.Remove(subscriberKey)) 
                        do logger.LogObserverCompletion(obs) } }
    member this.Restart() = 
        vm.Post(Kill)
        vm.Post(Resume)
    member this.Start() = this.Restart()
    member this.Resume() = vm.Post(Resume)
    member this.Deactivate() = vm.Post(Deactivate)
    member this.Suspend() = vm.Post(Suspend)
    member this.Kill() = vm.Post(Kill)
    member this.Post(m) = vm.Post(Action(m))
    member this.AsObservable = obs
