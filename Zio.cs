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
        public static ZIO<B> Select<A, B>
           (this ZIO<A> self, Func<A, B> f)
           => self.Map(f);

        public static ZIO<B> SelectMany<A, B>
           (this ZIO<A> self, Func<A, ZIO<B>> f)
           => self.FlatMap(f);

        public static ZIO<BB> SelectMany<A, B, BB>
           (this ZIO<A> self, Func<A, ZIO<B>> f, Func<A, B, BB> project)
           => self.FlatMap(a => f(a).Map(b => project(a, b)));
    }

    interface IContinuation<A, B>
    {
        ZIO<B> Apply(A value);
    }

    class Continuation<A, B> : ZIO<B>, IContinuation<A, B>
    {
        private Func<A, ZIO<B>> Func { get; }
        public Continuation(Func<A, ZIO<B>> func)
        {
            this.Func = func;
        }
        public ZIO<B> Apply(A value) => this.Func(value);
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
    enum ErrorChannel { Fail, Die } 

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

    interface Fiber<A>
    {
        // early completion
        // late completion
        ZIO<A> Join();
        //Unit Start();
    }

    // a fiber with the context necessary for evaluation
    internal class FiberContext<A> : Fiber<A>
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

        public ZIO<A> Join() =>
            ZIO.Async<Exit<A>>(callback => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join Before Await");
                Await(callback);
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join After Await");
                return Unit();
            }).FlatMap(ZIO.Done);
 
        dynamic currentZIO = null;
        private SynchronizationContext currentExecutor = null;
        private Stack<dynamic> stack = new Stack<dynamic>();
        private bool loop = true;

        public FiberContext(ZIO<A> startZio, SynchronizationContext startExecutor)
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
                    Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} FindNextErrorHandler Pop:{stack.Count}");
                    if (cont.Label == "Fold")
                    {
                        errorHandler = cont;
                        loop = false;
                    }
                }
            }
            return errorHandler;
        }

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
                            this.currentExecutor = currentZIO.Executor;
                            Continue(Unit());
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
                                this.currentZIO = errorHandler.Failure(currentZIO.Thunk());
                            }
                            break;

                        case "Fold":
                            stack.Push(currentZIO);
                            //Console.WriteLine("Push:" + stack.Count);
                            currentZIO = currentZIO.Zio;
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
            return Unit();
        }
    }

    // Declarative Encoding CHECK
    // Flatmap Stack Safety CHECK
    // Async Stack Safety
    // Concurrency Safety CHECK
    // Custom Execution Context CHECK
    // Interruption
    // Error Handling
    // Environment
    
    // ZIO<R, E, A>
    // Async
    interface ZIO<A> 
    {
        SynchronizationContext DefaultExecutor { get => new SynchronizationContext(); }
        
        internal sealed Fiber<A> UnsafeRunFiber() => 
            new FiberContext<A>(this, this.DefaultExecutor); 

        sealed Exit<A> UnsafeRunSync()
        {
            var latch = new CountdownEvent(1);
            var result = Exit.Default<A>();
            var zio = this.FoldCauseZIO(
                cause =>
                {
                    return ZIO.Succeed(() =>
                    {
                        result = Exit.Failure<A>(cause);
                        latch.Signal();
                        return Unit();
                    });
                },
                a => 
                {
                    return ZIO.Succeed(() =>
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

        ZIO<B> As<B>(B b) => 
            this.Map((_) => b);

        ZIO<A> CatchAll(Func<Exception, ZIO<A>> f) =>
            FoldZIO(e => f(e), a => ZIO.SucceedNow(a));

        ZIO<B> FlatMap<B>(Func<A, ZIO<B>> f) => 
            new FlatMap<A, B>(this, f);

        ZIO<B> Fold<B>(Func<Exception, B> failure, Func<A, B> success) =>
            FoldZIO(e => ZIO.SucceedNow(failure(e)), a => ZIO.SucceedNow(success(a)));

        ZIO<B> FoldZIO<B>(Func<Exception, ZIO<B>> failure, Func<A, ZIO<B>> success) =>
            FoldCauseZIO(cause =>
            {
                return cause.Channel switch
                {
                    ErrorChannel.Fail => failure(cause.Exception),
                    ErrorChannel.Die  => ZIO.FailCause<B>(() => cause),
                    _ => throw new Exception("Impossible")
                };
            }, success);

        ZIO<B> FoldCauseZIO<B>(Func<Cause, ZIO<B>> failure, Func<A, ZIO<B>> success) =>
            new Fold<A, B>(this, failure, success);

        ZIO<Unit> Forever()
        {
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Forever");
            return this.ZipRightF(() => Forever());
        }

        // correct by construction
        ZIO<Fiber<A>> Fork() =>
            new Fork<A>(this);

        ZIO<B> Map<B>(Func<A, B> f) => 
            this.FlatMap((a) => ZIO.SucceedNow(f(a)));

        ZIO<Unit> Repeat(int n)
        {
            Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Repeat:{n}");
            if (n <= 0) return ZIO.SucceedNow(Unit());
            else return this.ZipRightF(() => Repeat(n - 1));
        }

        ZIO<Unit> Shift(SynchronizationContext executor) =>
            new Shift(executor);

        ZIO<(A, B)> Zip<B>(ZIO<B> that) => 
            ZipWith(that, (a, b) => (a, b));

        ZIO<(A, B)> ZipPar<B>(ZIO<B> that) => 
            from f in this.Fork()
            from b in that
            from a in f.Join()
            select (a, b);
        
        ZIO<B> ZipRight<B>(ZIO<B> that) => 
            ZipWith(that, (a, b) => b);

        ZIO<B> ZipRightF<B>(Func<ZIO<B>> that) => 
            ZipWithF(that, (a, b) => b);

        ZIO<C> ZipWith<B, C>(ZIO<B> that, Func<A, B, C> f) => 
            from a in this
            from b in that
            select f(a, b);

        ZIO<C> ZipWithF<B, C>(Func<ZIO<B>> that, Func<A, B, C> f) => 
            from a in this
            from b in that()
            select f(a, b);
    }

    class SucceedNow<A> : ZIO<A>
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

    class Succeed<A> : ZIO<A>
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

    class FlatMap<A, B> : ZIO<B>, IContinuation<A, B>
    {
        public string Label
        {
            get => "FlatMap";
        }
        public ZIO<A> Zio { get; }
        public Func<A, ZIO<B>> Cont { get; }
        public FlatMap(ZIO<A> zio, Func<A, ZIO<B>> cont)
        {
            this.Zio = zio;
            this.Cont = cont;
        }

        public ZIO<B> Apply(A value) => this.Cont(value);
    }

    class Async<A> : ZIO<A>
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

    class Fork<A> : ZIO<Fiber<A>>
    {
        public string Label
        {
            get => "Fork";
        }
        public ZIO<A> Zio { get; }
        public Fork(ZIO<A> zio)
        {
            this.Zio = zio;
        }

        public FiberContext<A> CreateFiber(SynchronizationContext executor) =>
            new FiberContext<A>(Zio, executor);
    }

    class Shift : ZIO<Unit>
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

    class Fail<A> : ZIO<A>
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

    class Fold<A, B> : ZIO<B>, IContinuation<A, B>
    {
        public string Label
        {
            get => "Fold";
        }
        public ZIO<A> Zio { get; }
        public Func<A, ZIO<B>> Success { get; }
        public Func<Cause, ZIO<B>> Failure { get; }
        public Fold(ZIO<A> zio, Func<Cause, ZIO<B>> failure, Func<A, ZIO<B>> success)
        {
            this.Zio = zio;
            this.Failure = failure;
            this.Success = success;
        }

        public ZIO<B> Apply(A value) => this.Success(value);
    }

    static class ZIO
    {
        public static ZIO<A> Async<A>(Func<Func<A, Unit>, Unit> register) =>
            new Async<A>(register);
        public static ZIO<A> Fail<A>(Func<Exception> ex) =>
            new Fail<A>(() => new Cause(ex(), ErrorChannel.Fail));
        public static ZIO<A> FailCause<A>(Func<Cause> cause) =>
            new Fail<A>(cause);
        public static ZIO<Unit> Die(Exception ex) =>
            FailCause<Unit>(() => new Cause(ex, ErrorChannel.Die));
        public static ZIO<A> Done<A>(Exit<A> Exit) =>
            Exit.Match(ex => FailCause<A>(() => ex), a => SucceedNow(a));
        public static ZIO<A> Succeed<A>(Func<A> f) => 
            new Succeed<A>(f);
        internal static ZIO<A> SucceedNow<A>(A value) => 
            new SucceedNow<A>(value);
    }
}