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
            public List<Func<Exceptional<A>, Unit>> Callbacks { get; }
            public Running(List<Func<Exceptional<A>, Unit>> callbacks)
            {
                this.Callbacks = callbacks;
            }
        }
        class Done : FiberState
        {
            public Exceptional<A> Result { get; }
            public Done(Exceptional<A> result)
            {
                this.Result = result;
            }
        }
        // CAS
        // compare and swap
        private Akka.Util.AtomicReference<FiberState> state = 
            new Akka.Util.AtomicReference<FiberState>(new Running(new List<Func<Exceptional<A>, Unit>>()));

        public Unit Complete(Exceptional<A> result)
        {
            var loop = true;
            var toComplete = new List<Func<Exceptional<A>, Unit>>();
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

        public Unit Await(Func<Exceptional<A>, Unit> callback)
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
            ZIO.Async<Exceptional<A>>(callback => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join Before Await");
                Await(callback);
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join After Await");
                return Unit();
            }).FlatMap(ZIO.FromExceptional);
 
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
                Complete(Exceptional(value));
            }
            else
            {
                var cont = stack.Pop();
                //Console.WriteLine("Pop:" + stack.Count);
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
                        //Console.WriteLine("Push:" + stack.Count);
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
                            Func<Exceptional<A>, Unit> complete = (a) => Complete(a);
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
                            Complete(currentZIO.Thunk());
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

        sealed Exceptional<A> UnsafeRunSync()
        {
            var latch = new CountdownEvent(1);
            Exceptional<A> result = Exceptional(default(A));
            var zio = this.FoldZIO(
                ex =>
                {
                    return ZIO.Succeed(() =>
                    {
                        result = ex;
                        latch.Signal();
                        return Unit();
                    });
                },
                a => 
                {
                    return ZIO.Succeed(() =>
                    {
                        result = Exceptional(a);
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
            new Fold<A, B>(this, failure, success);

        // correct by construction
        ZIO<Fiber<A>> Fork() =>
            new Fork<A>(this);

        ZIO<B> Map<B>(Func<A, B> f) => 
            this.FlatMap((a) => ZIO.SucceedNow(f(a)));

        ZIO<Unit> Repeat(int n)
        {
            var count = n;
            var result = ZIO.SucceedNow(Unit());
            while (count > 0)
            {
                result = this.ZipRight<Unit>(result);
                count -= 1;
            }
            return result;
        }

        ZIO<Unit> RepeatUnsafe(int n)
        {
            if (n <= 0) return ZIO.SucceedNow(Unit());
            else return this.ZipRight(RepeatUnsafe(n - 1));
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

        ZIO<C> ZipWith<B, C>(ZIO<B> that, Func<A, B, C> f) => 
            from a in this
            from b in that
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

        public Unit Complete(Func<Exceptional<A>, Unit> complete) =>
            this.Register(a =>
            {
                return complete(Exceptional(a));
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
        public Func<Exception> Thunk { get; }
        public Fail(Func<Exception> thunk)
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
        public Func<Exception, ZIO<B>> Failure { get; }
        public Fold(ZIO<A> zio, Func<Exception, ZIO<B>> failure, Func<A, ZIO<B>> success)
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
        public static ZIO<A> Fail<A>(Func<Exception> e) =>
            new Fail<A>(e);
        public static ZIO<A> FromExceptional<A>(Exceptional<A> exceptional) =>
            exceptional.Match(ex => Fail<A>(() => ex), a => SucceedNow(a));
        public static ZIO<A> Succeed<A>(Func<A> f) => 
            new Succeed<A>(f);
        internal static ZIO<A> SucceedNow<A>(A value) => 
            new SucceedNow<A>(value);

    }
}