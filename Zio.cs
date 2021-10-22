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

    interface Fiber<A>
    {
        // early completion
        // late completion
        ZIO<A> Join();
        //Unit Start();
    }

    // a fiber with the context necessary for evaluation
    class FiberContext<A> : Fiber<A>
    {
        interface FiberState {}
        class Running : FiberState
        {
            public List<Func<A, Unit>> Callbacks { get; }
            public Running(List<Func<A, Unit>> callbacks)
            {
                this.Callbacks = callbacks;
            }
        }
        class Done : FiberState
        {
            public A Result { get; }
            public Done(A result)
            {
                this.Result = result;
            }
        }
        // CAS
        // compare and swap
        private Akka.Util.AtomicReference<FiberState> state = 
            new Akka.Util.AtomicReference<FiberState>(new Running(new List<Func<A, Unit>>()));

        public Unit Complete(A result)
        {
            var loop = true;
            var toComplete = new List<Func<A, Unit>>();
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

        public Unit Await(Func<A, Unit> callback)
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
            ZIO.Async<A>(callback => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join Before Await");
                Await(callback);
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join After Await");
                return Unit();
            });

        // public Unit Start()
        // {
        //     Task.Run(() =>
        //     { 
        //         this.zio.Run(a => 
        //         {
        //             Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Result:{a}");
        //             Complete(a);
        //             return Unit();
        //         });
        //     });
        //     return Unit();
        // }   
 
        dynamic currentZIO = null;
        private Stack<dynamic> stack = new Stack<dynamic>();
        private bool loop = true;

        public FiberContext(ZIO<A> startZio)
        {
            this.currentZIO = startZio;
            Task.Run(() => this.Run());
        }
        Unit Continue(dynamic value)
        {
            //Console.WriteLine(value);
            if (stack.Count == 0)
            {
                loop = false;
                Complete(value);
            }
            else
            {
                var cont = stack.Pop();
                //Console.WriteLine("Pop:" + stack.Count);
                currentZIO = cont(value);
            }
            return Unit();
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
                    case "Succeed":
                        Continue(currentZIO.Value);
                        break;
                    
                    case "Effect":
                        Continue(currentZIO.Thunk());
                        break;

                    case "FlatMap":
                        stack.Push(currentZIO.Cont);
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
                            Func<A, Unit> complete = (a) => Complete(a);
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
                        var fiber = currentZIO.CreateFiber();
                        Continue(fiber);
                        break;

                    default: 
                        throw new Exception("Zio case does not match");
                };
            }
            return Unit();
        }
    }

    class FiberImpl<A> : Fiber<A>
    {
        interface FiberState {}
        class Running : FiberState
        {
            public List<Func<A, Unit>> Callbacks { get; }
            public Running(List<Func<A, Unit>> callbacks)
            {
                this.Callbacks = callbacks;
            }
        }
        class Done : FiberState
        {
            public A Result { get; }
            public Done(A result)
            {
                this.Result = result;
            }
        }
        // CAS
        // compare and swap
        private Akka.Util.AtomicReference<FiberState> state = 
            new Akka.Util.AtomicReference<FiberState>(new Running(new List<Func<A, Unit>>()));

        public Unit Complete(A result)
        {
            var loop = true;
            var toComplete = new List<Func<A, Unit>>();
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

        public Unit Await(Func<A, Unit> callback)
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

        private ZIO<A> zio;

        public FiberImpl(ZIO<A> zio)
        {
            this.zio = zio;
        }

        public ZIO<A> Join() =>
            ZIO.Async<A>(callback => 
            {
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join Before Await");
                Await(callback);
                Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Join After Await");
                return Unit();
            });

        // public Unit Start()
        // {
        //     Task.Run(() =>
        //     { 
        //         this.zio.Run(a => 
        //         {
        //             Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId} Result:{a}");
        //             Complete(a);
        //             return Unit();
        //         });
        //     });
        //     return Unit();
        // }   
    }

    // Declarative Encoding CHECK
    // Stack Safety CHECK
    // Concurrency Safety CHECK
    // Custom Execution Context
    // Interruption
    // Error Handling
    // Environment
    
    // ZIO<R, E, A>
    // Async
    interface ZIO<A> 
    {
        sealed Fiber<A> UnsafeRunFiber() => 
            new FiberContext<A>(this); 

        sealed A UnsafeRunSync()
        {
            var latch = new CountdownEvent(1);
            var result = default(A);
            var zio = this.FlatMap(a => 
            {
                return ZIO.Succeed(() =>
                {
                    result = a;
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

        ZIO<B> FlatMap<B>(Func<A, ZIO<B>> f) => 
            new FlatMap<A, B>(this, f);

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

    class Succeed<A> : ZIO<A>
    {
        public string Label
        {
            get => "Succeed";
        }

        public A Value { get; }

        public Succeed(A value)
        {
            this.Value = value;
        }
    }

    class Effect<A> : ZIO<A>
    {
        public string Label
        {
            get => "Effect";
        }
        public Func<A> Thunk { get; }
        public Effect(Func<A> thunk)
        {
            this.Thunk = thunk;
        }
    }

    class FlatMap<A, B> : ZIO<B>
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

        public Unit Complete(Func<A, Unit> complete) =>
            this.Register(a =>
            {
                return complete(a);
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

        public FiberContext<A> CreateFiber() =>
            new FiberContext<A>(Zio);
    }

    static class ZIO
    {
        public static ZIO<A> Async<A>(Func<Func<A, Unit>, Unit> register) =>
            new Async<A>(register);
        public static ZIO<A> Succeed<A>(Func<A> value) => 
            new Effect<A>(value);
        internal static ZIO<A> SucceedNow<A>(A value) => 
            new Succeed<A>(value);
    }
}