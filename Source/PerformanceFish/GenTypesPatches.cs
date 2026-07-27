// Copyright (c) 2023 bradson
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PerformanceFish.Prepatching;

namespace PerformanceFish;

public sealed class GenTypesPatches : ClassWithFishPrepatches
{
	public sealed class AllTypes : FishPrepatch
	{
		public override string? Description { get; }
			= "Fix for thread safety. The vanilla method was rarely breaking and preventing the game from loading";

		public static readonly object Lock = new();
		
		public override MethodBase TargetMethodBase { get; }
			= AccessTools.PropertyGetter(typeof(GenTypes), nameof(GenTypes.AllTypes));

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void Prefix() => Monitor.Enter(Lock);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void Postfix() => Monitor.Exit(Lock); // TODO: replace with finalizer
	}

	public sealed class AllTypesWithAttribute : FishPrepatch
	{
		public override string? Description { get; }
			= "Fix for thread safety. The vanilla method was rarely breaking and preventing the game from loading";

		public static readonly object Lock = new();

		public override MethodBase TargetMethodBase { get; } = methodof(GenTypes.AllTypesWithAttribute<Attribute>);

		public override void Transpiler(ILProcessor ilProcessor, ModuleDefinition module)
			=> ilProcessor.ReplaceBodyWith(ReplacementBody<Attribute>);

		public static List<Type> ReplacementBody<TAttr>() where TAttr : Attribute
		{
			lock (Lock)
			{
				var typesWithAttribute = GenTypes.cachedTypesWithAttribute.TryGetValue(typeof(TAttr));

				if (typesWithAttribute is null)
				{
					// .AsParallel() removed - see the note in AllSubclasses below.
					GenTypes.cachedTypesWithAttribute.Add(typeof(TAttr),
						typesWithAttribute = GenTypes.AllTypes
							.Where(Predicate<TAttr>()).ToList());
				}

				return typesWithAttribute;
			}
		}

		public static Func<Type, bool> Predicate<TAttr>() where TAttr : Attribute
			=> static type => type.HasAttribute<TAttr>();

		static AllTypesWithAttribute()
		{
			// prevent beforefieldinit
		}
	}

	public sealed class AllSubclasses : FishPrepatch
	{
		public override string? Description { get; }
			= "Fix for thread safety. The vanilla method was rarely breaking and preventing the game from loading";

		public static readonly object Lock = new();

		public override MethodBase TargetMethodBase { get; } = methodof(GenTypes.AllSubclasses);

		public override void Transpiler(ILProcessor ilProcessor, ModuleDefinition module)
			=> ilProcessor.ReplaceBodyWith(ReplacementBody);

		public static List<Type> ReplacementBody(Type baseType)
		{
			lock (Lock)
			{
				var subclasses = GenTypes.cachedSubclasses.TryGetValue(baseType);

				if (subclasses is null)
				{
					// .AsParallel() removed - THIS is the 1.6 startup deadlock. The chain is
					//   GeneTrackerOptimization.SkippableTypes
					//     -> AllSubclassesNonAbstract.ReplacementBody
					//       -> GenTypes.AllSubclasses  ->  here.
					//
					// The main thread holds Lock and blocks waiting on PLINQ workers. Those
					// workers dereference the static field Predicate, which triggers this
					// class's type initializer - potentially still running on the main thread,
					// since the class was beforefieldinit. Workers wait on the type-init lock,
					// main thread waits on the workers, no CPU is consumed. Deadlock.
					//
					// There is a second reason parallelism is wrong here regardless:
					// PredicateClass.BaseType is SHARED MUTABLE STATE, reassigned on the line
					// above and then read concurrently by every worker. Two overlapping calls
					// with different baseTypes would silently produce wrong subclass lists.
					//
					// Sequential is correct and costs microseconds - it is one IsSubclassOf
					// call per type.
					((PredicateClass)Predicate.Target).BaseType = baseType;
					GenTypes.cachedSubclasses.Add(baseType,
						subclasses = GenTypes.AllTypes
							.Where(Predicate).ToList());
				}

				return subclasses;
			}
		}

		public static Func<Type, bool> Predicate = new PredicateClass().Invoke;

		static AllSubclasses()
		{
			// prevent beforefieldinit
		}

		public sealed class PredicateClass
		{
			public Type? BaseType;
			public bool Invoke(Type type) => type.IsSubclassOf(BaseType!);
		}
	}

	public sealed class AllSubclassesNonAbstract : FishPrepatch
	{
		public override string? Description { get; }
			= "Fix for thread safety, paired with a small optimization for faster loading. The vanilla method was "
			+ "rarely breaking and preventing the game from loading";

		public static readonly object Lock = new();

		public override MethodBase TargetMethodBase { get; } = methodof(GenTypes.AllSubclassesNonAbstract);

		public override void Transpiler(ILProcessor ilProcessor, ModuleDefinition module)
			=> ilProcessor.ReplaceBodyWith(ReplacementBody);

		public static List<Type> ReplacementBody(Type baseType)
		{
			lock (Lock)
			{
				var subclassesNonAbstract = GenTypes.cachedSubclassesNonAbstract.TryGetValue(baseType);

				if (subclassesNonAbstract is null)
				{
					// .AsParallel() removed - this was the 1.6 startup deadlock.
					//
					// PLINQ fans the predicate out onto ThreadPool threads WHILE the main
					// thread holds Lock and is blocked waiting for them to finish. The worker
					// threads touch the static field Predicate, which triggers this class's
					// type initializer. Because the class was beforefieldinit, that init can
					// still be in progress on the main thread - so the workers block on the
					// type-initializer lock, the main thread blocks on the workers, and
					// nothing burns CPU. Classic type-initializer/PLINQ deadlock.
					//
					// It only bit after Prepatcher reloaded the assemblies, because that is
					// when this replacement actually becomes GenTypes.AllSubclassesNonAbstract.
					// Before then the vanilla method runs and everything is fine - which is
					// exactly why the first init pass succeeded and the second one hung.
					//
					// The work here is one IsAbstract check per type. Parallelising it buys
					// microseconds and cost us a hung game.
					GenTypes.cachedSubclassesNonAbstract.Add(baseType,
						subclassesNonAbstract = baseType.AllSubclasses()
							.Where(Predicate).ToList());
				}

				return subclassesNonAbstract;
			}
		}

		public static Func<Type, bool> Predicate = static subClass => !subClass.IsAbstract;

		static AllSubclassesNonAbstract()
		{
			// prevent beforefieldinit - bradson uses this same idiom elsewhere in the mod.
			// Belt and braces alongside dropping AsParallel: it forces the static fields to
			// be initialised at a well-defined point rather than lazily mid-method.
		}
	}
}