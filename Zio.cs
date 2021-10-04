using System;
using Unit = System.ValueTuple;
using LaYumba.Functional;
using static LaYumba.Functional.F;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

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
        Unit Start();
    }

    class FiberImpl<A> : Fiber<A>
    {
        private ZIO<A> zio;
        private Option<A> maybeResult;

        private List<Func<A, Unit>> callbacks = new List<Func<A, Unit>>();

        public FiberImpl(ZIO<A> zio)
        {
            this.zio = zio;
        }

        public ZIO<A> Join() =>
            this.maybeResult.Match(
                ()  => ZIO.Async<A>(complete => 
                {
                    this.callbacks.Add(complete);
                    return Unit();
                }),
                (a) => ZIO.SucceedNow<A>(a)
            );

        public Unit Start()
        {
            Task.Run(() => this.zio.Run((a) => {
                this.maybeResult = Some(a);
                this.callbacks.ForEach(callback => callback(a));
                return Unit();
            }));
            return Unit();
        }
            
    }

    // Stack Safety
    // Execution Context
    // Concurrency Safety
    // Interruption
    // Error Handling
    // Environment
    // Declarative Encoding
    // ZIO<R, E, A>
    // Async
    interface ZIO<A> 
    {
        // continuation
        // callback
        // method A => Unit
        Unit Run(Func<A, Unit> callback);

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
            if (n <= 0) return ZIO.SucceedNow(Unit());
            else return this.ZipRight(Repeat(n -1));
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
        private A value;
        public Succeed(A value)
        {
            this.value = value;
        }

        public Unit Run(Func<A, Unit> callback)
            => callback(this.value);
    }

    class Effect<A> : ZIO<A>
    {
        private Func<A> f;
        public Effect(Func<A> f)
        {
            this.f = f;
        }

        public Unit Run(Func<A, Unit> callback)
            => callback(this.f());
    }

    class FlatMap<A, B> : ZIO<B>
    {
        private ZIO<A> a;
        private Func<A, ZIO<B>> f;
        public FlatMap(ZIO<A> a, Func<A, ZIO<B>> f)
        {
            this.a = a;
            this.f = f;
        }

        public Unit Run(Func<B, Unit> callback)
        {
            return this.a.Run(a => 
            {
                return f(a).Run(callback);
            });
        }
    }

    class Async<A> : ZIO<A>
    {
        private Func<Func<A, Unit>, Unit> register;
        public Async(Func<Func<A, Unit>, Unit> register)
        {
            this.register = register;
        }

        public Unit Run(Func<A, Unit> callback) =>
            this.register(callback);
    }

    class Fork<A> : ZIO<Fiber<A>>
    {
        private ZIO<A> zio;
        public Fork(ZIO<A> zio)
        {
            this.zio = zio;
        }

        public Unit Run(Func<Fiber<A>, Unit> callback)
        {
            var fiber = new FiberImpl<A>(zio);
            fiber.Start();
            return callback(fiber);
        }
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