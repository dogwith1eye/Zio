using System;
using Unit = System.ValueTuple;
using LaYumba.Functional;
using static LaYumba.Functional.F;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace Zio
{
    static class ZIOWorkflow
    {
        public static ZIO<R, B> Select<R, A, B>
           (this ZIO<R, A> self, Func<A, B> f)
           => self.Map(f);

        public static ZIO<R, B> SelectMany<R, A, B>
           (this ZIO<R, A> self, Func<A, ZIO<R, B>> f)
           => self.FlatMap(f);

        public static ZIO<R, BB> SelectMany<R, A, B, BB>
           (this ZIO<R, A> self, Func<A, ZIO<R, B>> f, Func<A, B, BB> project)
           => self.FlatMap(a => f(a).Map(b => project(a, b)));
    }

    interface IContinuation<R, A, B>
    {
        ZIO<R, B> Apply(A value);
    }

    class Continuation<R, A, B> : ZIO<R, B>, IContinuation<R, A, B>
    {
        private Func<A, ZIO<R, B>> Func { get; }
        public Continuation(Func<A, ZIO<R, B>> func)
        {
            this.Func = func;
        }
        public ZIO<R, B> Apply(A value) => this.Func(value);
    }

    class Cause 
    {
        public Exception Exception { get; }
        public ErrorChannel Channel { get; }
        public Cause(Exception exception, ErrorChannel channel)
        {
            this.Exception = exception;
            this.Channel = channel;
        }
    }
    enum ErrorChannel { Fail, Die, Interrupt } 

    struct Exit<A>
    {
        internal Cause Ex { get; }
        internal A Value { get; }
        
        public bool Success => Ex == null;
        public bool Failure => Ex != null;

        internal Exit(Cause ex)
        {
            if (ex == null) throw new ArgumentNullException(nameof(ex));
            Ex = ex;
            Value = default(A);
        }

        internal Exit(A right)
        {
            Value = right;
            Ex = null;
        }

        public B Match<B>(Func<Cause, B> Cause, Func<A, B> Success)
         => this.Failure ? Cause(Ex) : Success(Value);

        public Unit Match(Action<Cause> Cause, Action<A> Success)
            => Match(Cause.ToFunc(), Success.ToFunc());

        public override string ToString() 
            => Match(
                ex => $"Failure({ex.Channel}({ex.Exception.Message}))",
                t => $"Success({t})");
    }

    static class Exit
    {
        public static Exit<A> Default<A>() =>
            new Exit<A>(default(A));
        public static Exit<A> Fail<A>(Exception ex) =>
            Exit.Failure<A>(new Cause(ex, ErrorChannel.Fail));
        public static Exit<A> Failure<A>(Cause cause) => 
            new Exit<A>(cause);
        public static Exit<A> Succeed<A>(A value) => 
            new Exit<A>(value);
    }

    interface Fiber<R, A>
    {
        // early completion
        // late completion
        ZIO<R, A> Join();
        ZIO<R, Unit> Interrupt();
    }

    // a fiber with the context necessary for evaluation
    internal class FiberContext<R, A> : Fiber<R, A>
    {
        interface FiberState {}
        class Running : FiberState
        {
            public List<Func<Exit<A>, Unit>> Callbacks { get; }
            public Running(List<Func<Exit<A>, Unit>> callbacks)
            {
                this.Callbacks = callbacks;
            }
        }
        class Done : FiberState
        {
            public Exit<A> Result { get; }
            public Done(Exit<A> result)
            {
                this.Result = result;
            }
        }
        // CAS
        // compare and swap
        private Akka.Util.AtomicReference<FiberState> state = 
            new Akka.Util.AtomicReference<FiberState>(new Running(new List<Func<Exit<A>, Unit>>()));

        public Unit Complete(Exit<A> result)
        {
            var loop = true;
            var toComplete = new List<Func<Exit<A>, Unit>>();
            while (loop)
            {
                var oldState = state.Value;
                switch (oldState)
                {
                    case Running running:
                        //Console.WriteLine("Complete Pending callbacks count:" + running.Callbacks.Count());
                        toComplete = running.Callbacks;
                        var tryAgain = !state.CompareAndSet(oldState, new Done(result));
                        //Console.WriteLine("Complete tryAgain:" + tryAgain);
                        loop = tryAgain;
                        break;
                    case Done done:
                        throw new Exception("Fiber being completed multiple times");
                }
            }
            toComplete.ForEach(callback => callback(result));
            
            return Unit();
        }

        public Unit Await(Func<Exit<A>, Unit> callback)
        {
            //Console.WriteLine("Await with our callback");
            var loop = true;
            while (loop)
            {
                var oldState = state.Value;
                switch (oldState)
                {
                    case Running running:
                        //Console.WriteLine("Await already running Count:" + running.Callbacks.Count());
                        running.Callbacks.Add(callback);
                        var newState = new Running(running.Callbacks);
                        loop = !state.CompareAndSet(oldState, newState);
                        Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join Register");
                        break;
                    case Done done:
                        Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join Done");
                        callback(done.Result);
                        loop = false;
                        break;
                }
            }
            return Unit();
        }

        public ZIO<R, A> Join() =>
            ZIO.Async<R, Exit<A>>(callback => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join Before Await");
                Await(callback);
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join After Await");
                return Unit();
            }).FlatMap(ZIO.Done<R, A>);
 
        dynamic currentZIO = null;
        private SynchronizationContext currentExecutor = null;
        private Stack<dynamic> stack = new Stack<dynamic>();
        private Stack<dynamic> envStack = new Stack<dynamic>();
        private bool loop = true;

        public FiberContext(ZIO<R, A> startZio, SynchronizationContext startExecutor)
        {
            this.currentZIO = startZio;
            this.currentExecutor = startExecutor;
            this.currentExecutor.Post(_ => this.Run(), null);
        }
        Unit Continue(dynamic value)
        {
            //Console.WriteLine(value);
            if (stack.Count == 0)
            {
                loop = false;
                Complete(Exit.Succeed<A>(value));
            }
            else
            {
                var cont = stack.Pop();
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Continue Pop:{stack.Count}");
                currentZIO = cont.Apply(value);
            }
            return Unit();
        }

        dynamic FindNextErrorHandler()
        {
            var loop = true;
            dynamic errorHandler = null;
            while (loop)
            {
                if (stack.Count == 0)
                {
                    loop = false;
                }
                else
                {
                    var cont = stack.Pop();
                    Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} FindNextErrorHandler Pop:{stack.Count} Label:{cont.Label}");
                    if (cont.Label == "Fold")
                    {
                        errorHandler = cont;
                        loop = false;
                    }
                }
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Loop:{stack.Count} Label:{loop}");     
            }
            return errorHandler;
        } 
        
        // has someone sent us the signal to stop executing
        private long _interrupted = 0;
        public bool Interrupted 
        {
            get
            {
                /* Interlocked.Read() is only available for int64,
                * so we have to represent the bool as a long with 0's and 1's
                */
                return Interlocked.Read(ref _interrupted) == 1;
            }
            set
            {
                Interlocked.Exchange(ref _interrupted, Convert.ToInt64(value));
            }
        }

        // are we in the process of finalizing ourselves
        private long _interrupting = 0;
        public bool Interrupting
        {
            get
            {
                /* Interlocked.Read() is only available for int64,
                * so we have to represent the bool as a long with 0's and 1's
                */
                return Interlocked.Read(ref _interrupting) == 1;
            }
            set
            {
                Interlocked.Exchange(ref _interrupting, Convert.ToInt64(value));
            }
        }

         // are we in a region where we are subject to being interrupted
        private long _interruptible = 1;
        public bool Interruptible
        {
            get
            {
                /* Interlocked.Read() is only available for int64,
                * so we have to represent the bool as a long with 0's and 1's
                */
                return Interlocked.Read(ref _interruptible) == 1;
            }
            set
            {
                Interlocked.Exchange(ref _interruptible, Convert.ToInt64(value));
            }
        }

        bool ShouldInterrupt() => Interrupted && Interruptible && !Interrupting;

        public ZIO<R, Unit> Interrupt() =>
            ZIO.Succeed<R, Unit>(() => 
            {
                Interrupted = true;
                return Unit();
            });

        Unit Resume(dynamic nextZIO)
        {
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Resume");
            currentZIO = nextZIO;
            loop = true;
            Run();
            return Unit();
        }

        Unit Run()
        {
            while (loop)
            {
                if (ShouldInterrupt())
                {
                    Interrupting = true;
                    stack.Push(currentZIO);
                    currentZIO = ZIO.FailCause<Unit>(() => new Cause(null, ErrorChannel.Interrupt));
                }
                else
                {
                    try
                    {
                        switch (currentZIO.Label)
                        {
                            case "SucceedNow":
                                Continue(currentZIO.Value);
                                break;
                            
                            case "Succeed":
                                Continue(currentZIO.Thunk());
                                break;

                            case "FlatMap":
                                stack.Push(currentZIO);
                                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Push:{stack.Count}");
                                currentZIO = currentZIO.Zio;
                                break;

                            case "Async":
                                // we are always done with handling the run loop on the current thread 
                                loop = false;
                                // continue with the callback the user provided on the current thread
                                if (stack.Count == 0)
                                {      
                                    Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Async Run Start");  
                                    //currentZIO.Register(callback);
                                    Func<Exit<A>, Unit> complete = (a) => Complete(a);
                                    currentZIO.Complete(complete);
                                    Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Async Run End");
                                }
                                // stack not empty so continue with our run loop on the thread left doing work
                                else
                                {
                                    Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Async Run Start {stack.Count()}");
                                    Func<dynamic, Unit> resume = (dyn) => Resume(dyn);
                                    currentZIO.Resume(resume);
                                    Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Async Run End {stack.Count()}");
                                }
                                break;

                            case "Fork":
                                var fiber = currentZIO.CreateFiber(this.currentExecutor);
                                Continue(fiber);
                                break;

                            case "Shift":
                                currentExecutor = currentZIO.Executor;
                                Continue(Unit());
                                break;

                            case "SetInterruptStatus":
                                var oldInterrruptible = this.Interruptible;
                                Interruptible = InterruptStatusEnum.ToBoolean(this.currentZIO.InterruptStatus);
                                var ifinalizer = ZIO.Succeed(() =>
                                {
                                    Interruptible = oldInterrruptible;
                                    return Unit();
                                });
                                currentZIO = currentZIO.Ensuring(ifinalizer);
                                break;

                            case "Fail":
                                var errorHandler = FindNextErrorHandler();
                                if (errorHandler is null)
                                {
                                    loop = false;
                                    Complete(Exit.Fail(currentZIO.Thunk()));
                                }
                                else
                                {
                                    currentZIO = errorHandler.Failure(currentZIO.Thunk());
                                }
                                break;

                            case "Fold":
                                stack.Push(currentZIO);
                                //Console.WriteLine("Push:" + stack.Count);
                                currentZIO = currentZIO.Zio;
                                break;

                            case "Provide":
                                envStack.Push(currentZIO.Environment);
                                var pfinalizer = ZIO.Succeed<R, Unit>(() =>
                                {
                                    envStack.Pop();
                                    return Unit();
                                });
                                currentZIO = currentZIO.Ensuring(pfinalizer);
                                break;

                            case "Access":
                                var currentEnvironment = envStack.Peek();
                                currentZIO = currentZIO.Thunk(currentEnvironment);
                                break;

                            default: 
                                throw new Exception("Zio case does not match");
                        };
                    }
                    catch (Exception ex)
                    {
                        this.currentZIO = ZIO.Die(ex);
                    }
                }
            }
            return Unit();
        }
    }

    // Declarative Encoding CHECK
    // Flatmap Stack Safety CHECK
    // Concurrency Safety CHECK
    // Custom Execution Context CHECK
    // Error Handling CHECK
    // Interruption CHECK
    // Environment
    // Stack as a function to a ZIO
    // Async Stack Safety
    // Heap growth forever
    // Switch to tags
    // Main has to wait for finalizer
    // Uninterruptible mask (region which reverts to original status) (bracket in ZIO1, applyAndRelease ZIO2)
    interface ZIO<R, A> 
    {
        SynchronizationContext DefaultExecutor { get => new SynchronizationContext(); }
        
        internal sealed Fiber<R, A> UnsafeRunFiber() => 
            new FiberContext<R, A>(this, this.DefaultExecutor); 

        sealed Exit<A> UnsafeRunSync()
        {
            var latch = new CountdownEvent(1);
            var result = Exit.Default<A>();
            var zio = this.FoldCauseZIO(
                cause =>
                {
                    return ZIO.Succeed<R, Unit>(() =>
                    {
                        result = Exit.Failure<A>(cause);
                        latch.Signal();
                        return Unit();
                    });
                },
                a => 
                {
                    return ZIO.Succeed<R, Unit>(() =>
                    {
                        result = Exit.Succeed(a);
                        latch.Signal();
                        return Unit();
                    });
                });
            zio.UnsafeRunFiber();
            latch.Wait();
            return result;
        }

        ZIO<R, B> As<B>(B b) => 
            this.Map((_) => b);

        ZIO<R, A> CatchAll(Func<Exception, ZIO<R, A>> f) =>
            FoldZIO(e => f(e), a => ZIO.SucceedNow<R, A>(a));

        ZIO<R, A> Ensuring(ZIO<R, Unit> finalizer) =>
            FoldCauseZIO(
                cause => finalizer.ZipRight(ZIO.FailCause<R, A>(() => cause)),
                a => finalizer.ZipRight(ZIO.SucceedNow<R, A>(a)));

        ZIO<R, B> FlatMap<B>(Func<A, ZIO<R, B>> f) => 
            new FlatMap<R, A, B>(this, f);

        ZIO<R, B> Fold<B>(Func<Exception, B> failure, Func<A, B> success) =>
            FoldZIO(e => ZIO.SucceedNow<R, B>(failure(e)), a => ZIO.SucceedNow<R, B>(success(a)));

        ZIO<R, B> FoldZIO<B>(Func<Exception, ZIO<R, B>> failure, Func<A, ZIO<R, B>> success) =>
            FoldCauseZIO(cause =>
            {
                return cause.Channel switch
                {
                    ErrorChannel.Fail => failure(cause.Exception),
                    ErrorChannel.Die  => ZIO.FailCause<R, B>(() => cause),
                    _ => throw new Exception("Impossible")
                };
            }, success);

        ZIO<R, B> FoldCauseZIO<B>(Func<Cause, ZIO<R, B>> failure, Func<A, ZIO<R, B>> success) =>
            new Fold<R, A, B>(this, failure, success);

        ZIO<R, Unit> Forever()
        {
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Forever");
            return this.ZipRightF(() => Forever());
        }

        // correct by construction
        ZIO<R, Fiber<R, A>> Fork() =>
            new Fork<R, A>(this);

        ZIO<R, B> Map<B>(Func<A, B> f) => 
            this.FlatMap((a) => ZIO.SucceedNow<R, B>(f(a)));

        ZIO<R, A> Provide(R r) =>
            new Provide<R, A>(this, r);

        ZIO<R, Unit> Repeat(int n)
        {
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Repeat:{n}");
            if (n <= 0) return ZIO.SucceedNow<R, Unit>(Unit());
            else return this.ZipRightF(() => Repeat(n - 1));
        }

        ZIO<R, A> SetInterruptStatus(InterruptStatus status) =>
            new SetInterruptStatus<R, A>(this, status);

        ZIO<R, A> Interruptible() =>
            SetInterruptStatus(InterruptStatus.Interruptible);

        ZIO<R, A> Uninterruptible() =>
            SetInterruptStatus(InterruptStatus.Uninterruptible);

        ZIO<R, Unit> Shift(SynchronizationContext executor) =>
            new Shift<R>(executor);

        ZIO<R, (A, B)> Zip<B>(ZIO<R, B> that) => 
            ZipWith(that, (a, b) => (a, b));

        ZIO<R, (A, B)> ZipPar<B>(ZIO<R, B> that) => 
            from f in this.Fork()
            from b in that
            from a in f.Join()
            select (a, b);
        
        ZIO<R, B> ZipRight<B>(ZIO<R, B> that) => 
            ZipWith(that, (a, b) => b);

        ZIO<R, B> ZipRightF<B>(Func<ZIO<R, B>> that) => 
            ZipWithF(that, (a, b) => b);

        ZIO<R, C> ZipWith<B, C>(ZIO<R, B> that, Func<A, B, C> f) => 
            from a in this
            from b in that
            select f(a, b);

        ZIO<R, C> ZipWithF<B, C>(Func<ZIO<R, B>> that, Func<A, B, C> f) => 
            from a in this
            from b in that()
            select f(a, b);
    }

    class SucceedNow<R, A> : ZIO<R, A>
    {
        public string Label
        {
            get => "SucceedNow";
        }

        public A Value { get; }

        public SucceedNow(A value)
        {
            this.Value = value;
        }
    }

    class Succeed<R, A> : ZIO<R, A>
    {
        public string Label
        {
            get => "Succeed";
        }
        public Func<A> Thunk { get; }
        public Succeed(Func<A> thunk)
        {
            this.Thunk = thunk;
        }
    }

    class FlatMap<R, A, B> : ZIO<R, B>, IContinuation<R, A, B>
    {
        public string Label
        {
            get => "FlatMap";
        }
        public ZIO<R, A> Zio { get; }
        public Func<A, ZIO<R, B>> Cont { get; }
        public FlatMap(ZIO<R, A> zio, Func<A, ZIO<R, B>> cont)
        {
            this.Zio = zio;
            this.Cont = cont;
        }

        public ZIO<R, B> Apply(A value) => this.Cont(value);
    }

    class Async<R, A> : ZIO<R, A>
    {
        public string Label
        {
            get => "Async";
        }
        public Func<Func<A, Unit>, Unit> Register { get; }
        public Async(Func<Func<A, Unit>, Unit> register)
        {
            this.Register = register;
        }

        public Unit Resume(Func<dynamic, Unit> resume) =>
            this.Register(a =>
            {
                return resume(ZIO.SucceedNow(a));
            });

        public Unit Complete(Func<Exit<A>, Unit> complete) =>
            this.Register(a =>
            {
                return complete(Exit.Succeed<A>(a));
            });
    }

    class Fork<R, A> : ZIO<R, Fiber<R, A>>
    {
        public string Label
        {
            get => "Fork";
        }
        public ZIO<R, A> Zio { get; }
        public Fork(ZIO<R, A> zio)
        {
            this.Zio = zio;
        }

        public FiberContext<R, A> CreateFiber(SynchronizationContext executor) =>
            new FiberContext<R, A>(Zio, executor);
    }

    class Shift<R> : ZIO<R, Unit>
    {
        public string Label
        {
            get => "Shift";
        }
        public SynchronizationContext Executor { get; }
        public Shift(SynchronizationContext executor)
        {
            this.Executor = executor;
        }
    }

    class Fail<R, A> : ZIO<R, A>
    {
        public string Label
        {
            get => "Fail";
        }
        public Func<Cause> Thunk { get; }
        public Fail(Func<Cause> thunk)
        {
            this.Thunk = thunk;
        }
    }

    class Fold<R, A, B> : ZIO<R, B>, IContinuation<R, A, B>
    {
        public string Label
        {
            get => "Fold";
        }
        public ZIO<R, A> Zio { get; }
        public Func<A, ZIO<R, B>> Success { get; }
        public Func<Cause, ZIO<R, B>> Failure { get; }
        public Fold(ZIO<R, A> zio, Func<Cause, ZIO<R, B>> failure, Func<A, ZIO<R, B>> success)
        {
            this.Zio = zio;
            this.Failure = failure;
            this.Success = success;
        }

        public ZIO<R, B> Apply(A value) => this.Success(value);
    }

    enum InterruptStatus { Interruptible, Uninterruptible }

    static class InterruptStatusEnum
    {
        public static bool ToBoolean(InterruptStatus status) => status switch
        {
            
            InterruptStatus.Interruptible => true,
            InterruptStatus.Uninterruptible => false,
            _ => throw new Exception("Impossible")
        };
    }

    class SetInterruptStatus<R, A> : ZIO<R, A>
    {
        public string Label
        {
            get => "SetInterruptStatus";
        }
        public ZIO<R, A> Zio { get; }
        public InterruptStatus InterruptStatus { get; }

        public ZIO<R, A> Ensuring(ZIO<R, Unit> finalizer) =>
            Zio.Ensuring(finalizer);

        public SetInterruptStatus(ZIO<R, A> zio, InterruptStatus interruptStatus)
        {
            this.Zio = zio;
            this.InterruptStatus = interruptStatus;
        }
    }

    class Provide<R, A> : ZIO<R, A>
    {
        public string Label
        {
            get => "Provide";
        }
        public ZIO<R, A> Zio { get; }
        public R Environment { get; }
        public ZIO<R, A> Ensuring(ZIO<R, Unit> finalizer) =>
            Zio.Ensuring(finalizer);
        public Provide(ZIO<R, A> Zio, R environment)
        {
            this.Zio = Zio;
            this.Environment = environment;
        }
    }

    class Access<R, A> : ZIO<R, A>
    {
        public string Label
        {
            get => "Access";
        }
        public Func<R, ZIO<R, A>> Thunk { get; }
        public Access(Func<R, ZIO<R, A>> thunk)
        {
            this.Thunk = thunk;
        }
    }

    static class ZIO
    {
        public static ZIO<R, R> Environment<R>() =>
            AccessZIO<R, R>(env => SucceedNow<R, R>(env));
        public static ZIO<Unit, A> AccessZIO<A>(Func<Unit, ZIO<Unit, A>> f) =>
            new Access<Unit, A>(f);
        public static ZIO<R, A> AccessZIO<R, A>(Func<R, ZIO<R, A>> f) =>
            new Access<R, A>(f);
        public static ZIO<Unit, A> Async<A>(Func<Func<A, Unit>, Unit> register) =>
            new Async<Unit, A>(register);
        public static ZIO<R, A> Async<R, A>(Func<Func<A, Unit>, Unit> register) =>
            new Async<R, A>(register);
        public static ZIO<Unit, A> Fail<A>(Func<Exception> ex) =>
            new Fail<Unit, A>(() => new Cause(ex(), ErrorChannel.Fail));
        public static ZIO<R, A> Fail<R, A>(Func<Exception> ex) =>
            new Fail<R, A>(() => new Cause(ex(), ErrorChannel.Fail));
        public static ZIO<Unit, A> FailCause<A>(Func<Cause> cause) =>
            new Fail<Unit, A>(cause);
        public static ZIO<R, A> FailCause<R, A>(Func<Cause> cause) =>
            new Fail<R, A>(cause);
        public static ZIO<Unit, Unit> Die(Exception ex) =>
            FailCause<Unit, Unit>(() => new Cause(ex, ErrorChannel.Die));
        public static ZIO<R, Unit> Die<R>(Exception ex) =>
            FailCause<R, Unit>(() => new Cause(ex, ErrorChannel.Die));
        public static ZIO<Unit, A> Done<A>(Exit<A> Exit) =>
            Exit.Match(ex => FailCause<A>(() => ex), a => SucceedNow(a));
        public static ZIO<R, A> Done<R, A>(Exit<A> Exit) =>
            Exit.Match(ex => FailCause<R, A>(() => ex), a => SucceedNow<R, A>(a));
        public static ZIO<Unit, A> Succeed<A>(Func<A> f) => 
            new Succeed<Unit, A>(f);
        public static ZIO<R, A> Succeed<R, A>(Func<A> f) => 
            new Succeed<R, A>(f);
        internal static ZIO<Unit, A> SucceedNow<A>(A value) => 
            new SucceedNow<Unit, A>(value);
        internal static ZIO<R, A> SucceedNow<R, A>(A value) => 
            new SucceedNow<R, A>(value);
    }
}