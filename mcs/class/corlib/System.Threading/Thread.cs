//
// System.Threading.Thread.cs
//
// Author:
//   Dick Porter (dick@ximian.com)
//
// (C) Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004-2006 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.Runtime.Remoting.Contexts;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Permissions;
using System.Security.Principal;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.IO;
using System.Collections;
using System.Security;

#if NET_2_0
using System.Runtime.ConstrainedExecution;
#endif

namespace System.Threading {

	[ClassInterface (ClassInterfaceType.None)]
#if NET_2_0
	[ComVisible (true)]
	[ComDefaultInterface (typeof (_Thread))]
	public sealed class Thread : CriticalFinalizerObject, _Thread {
#else
	public sealed class Thread : _Thread {
#endif

		#region Sync with metadata/object-internals.h
		int lock_thread_id;
		// stores a thread handle
		private IntPtr system_thread_handle;

		/* Note this is an opaque object (an array), not a CultureInfo */
		private object cached_culture_info;
		private IntPtr unused0;
		private bool threadpool_thread;
		/* accessed only from unmanaged code */
		private IntPtr name;
		private int name_len; 
		private ThreadState state = ThreadState.Unstarted;
		private object abort_exc;
		internal object abort_state;
		/* thread_id is only accessed from unmanaged code */
		private Int64 thread_id;
		
		/* start_notify is used by the runtime to signal that Start()
		 * is ok to return
		 */
		private IntPtr start_notify;
		private IntPtr stack_ptr;
		private UIntPtr static_data; /* GC-tracked */
		private IntPtr jit_data;
		private IntPtr lock_data;
		Context current_appcontext;
		int stack_size;
		object start_obj;
		private IntPtr appdomain_refs;
		private bool interruption_requested;
		private IntPtr suspend_event;
		private IntPtr suspended_event;
		private IntPtr resume_event;
		private IntPtr synch_cs;
		private IntPtr serialized_culture_info;
		private int serialized_culture_info_len;
		private IntPtr serialized_ui_culture_info;
		private int serialized_ui_culture_info_len;
		private ExecutionContext _ec;
		private bool thread_dump_requested;
		private IntPtr end_stack;
		private bool thread_interrupt_requested;
		private byte apartment_state = (byte)ApartmentState.Unknown;
		/* 
		 * These fields are used to avoid having to increment corlib versions
		 * when a new field is added to the unmanaged MonoThread structure.
		 */
		private IntPtr small_id;
		private IntPtr manage_callback;
		private IntPtr unused7;
		volatile int critical_region_level;
		#endregion

		// the name of local_slots is important as it's used by the runtime.
		[ThreadStatic] 
		static object[] local_slots;

		// can be both a ThreadSart and a ParameterizedThreadStart
		private MulticastDelegate threadstart;
		private string thread_name=null;
		
		private static int _managed_id_counter;
		private int managed_id;
		
		private IPrincipal _principal;
		internal NumberFormatter _numberFormatter;

		public static Context CurrentContext {
			[SecurityPermission (SecurityAction.LinkDemand, Infrastructure=true)]
			get {
				return(AppDomain.InternalGetContext ());
			}
		}

		public static IPrincipal CurrentPrincipal {
			get {
				IPrincipal p = null;
				Thread th = CurrentThread;
				lock (th) {
					p = th._principal;
					if (p == null) {
						p = GetDomain ().DefaultPrincipal;
						th._principal = p;
					}
				}
				return p;
			}
			[SecurityPermission (SecurityAction.Demand, ControlPrincipal = true)]
			set {
				CurrentThread._principal = value;
			}
		}

		// Looks up the object associated with the current thread
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static Thread CurrentThread_internal();
		
		public static Thread CurrentThread {
#if NET_2_0
			[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
#endif
			get {
				return(CurrentThread_internal());
			}
		}

		internal static int CurrentThreadId {
			get {
				return (int)(CurrentThread.thread_id);
			}
		}

		// Stores a hash keyed by strings of LocalDataStoreSlot objects
		static Hashtable datastorehash;
		private static object datastore_lock = new object ();
		
		private static void InitDataStoreHash () {
			lock (datastore_lock) {
				if (datastorehash == null) {
					datastorehash = Hashtable.Synchronized(new Hashtable());
				}
			}
		}
		
		public static LocalDataStoreSlot AllocateNamedDataSlot (string name) {
			lock (datastore_lock) {
				if (datastorehash == null)
					InitDataStoreHash ();
				LocalDataStoreSlot slot = (LocalDataStoreSlot)datastorehash [name];
				if (slot != null) {
					// This exception isnt documented (of
					// course) but .net throws it
					throw new ArgumentException("Named data slot already added");
				}
			
				slot = AllocateDataSlot ();

				datastorehash.Add (name, slot);

				return slot;
			}
		}

		public static void FreeNamedDataSlot (string name) {
			lock (datastore_lock) {
				if (datastorehash == null)
					InitDataStoreHash ();
				LocalDataStoreSlot slot = (LocalDataStoreSlot)datastorehash [name];

				if (slot != null) {
					datastorehash.Remove (slot);
				}
			}
		}

		public static LocalDataStoreSlot AllocateDataSlot () {
			return new LocalDataStoreSlot (true);
		}

		public static object GetData (LocalDataStoreSlot slot) {
			object[] slots = local_slots;
			if (slot == null)
				throw new ArgumentNullException ("slot");
			if (slots != null && slot.slot < slots.Length)
				return slots [slot.slot];
			return null;
		}

		public static void SetData (LocalDataStoreSlot slot, object data) {
			object[] slots = local_slots;
			if (slot == null)
				throw new ArgumentNullException ("slot");
			if (slots == null) {
				slots = new object [slot.slot + 2];
				local_slots = slots;
			} else if (slot.slot >= slots.Length) {
				object[] nslots = new object [slot.slot + 2];
				slots.CopyTo (nslots, 0);
				slots = nslots;
				local_slots = slots;
			}
			slots [slot.slot] = data;
		}

		internal NumberFormatter AcquireNumberFormatter() {
			NumberFormatter res = _numberFormatter;
			_numberFormatter = null;
			if (res == null)
				return new NumberFormatter (this);
			return res;
		}

		internal void ReleaseNumberFormatter (NumberFormatter formatter) {
			_numberFormatter = formatter;
		}

		public static AppDomain GetDomain() {
			return AppDomain.CurrentDomain;
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		public extern static int GetDomainID();

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		internal extern static void FreeLocalSlotValues (int slot, bool thread_local);

		public static LocalDataStoreSlot GetNamedDataSlot(string name) {
			lock (datastore_lock) {
				if (datastorehash == null)
					InitDataStoreHash ();
				LocalDataStoreSlot slot=(LocalDataStoreSlot)datastorehash[name];

				if(slot==null) {
					slot=AllocateNamedDataSlot(name);
				}
			
				return(slot);
			}
		}
		
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static void ResetAbort_internal();

		[SecurityPermission (SecurityAction.Demand, ControlThread=true)]
		public static void ResetAbort ()
		{
			ResetAbort_internal ();
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern static void Sleep_internal(int ms);

		public static void Sleep(int millisecondsTimeout) {
			if((millisecondsTimeout<0) && (millisecondsTimeout != Timeout.Infinite)) {
				throw new ArgumentException("Negative timeout");
			}
			Sleep_internal(millisecondsTimeout);
		}

		public static void Sleep(TimeSpan timeout) {
			// LAMESPEC: says to throw ArgumentException too
			int ms=Convert.ToInt32(timeout.TotalMilliseconds);
			
			if(ms < 0 || ms > Int32.MaxValue) {
				throw new ArgumentOutOfRangeException("Timeout out of range");
			}

			Sleep_internal(ms);
		}

		// Returns the system thread handle
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern IntPtr Thread_internal (MulticastDelegate start);

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern void Thread_init ();

		private static int GetNewManagedId() {
			return Interlocked.Increment(ref _managed_id_counter);
		}

		public Thread(ThreadStart start) {
			if(start==null) {
				throw new ArgumentNullException("Null ThreadStart");
			}
			threadstart=start;

			Thread_init ();
		}

#if NET_2_0
		[Obsolete ("Deprecated in favor of GetApartmentState, SetApartmentState and TrySetApartmentState.")]
#endif
		public ApartmentState ApartmentState {
			get {
				if ((ThreadState & ThreadState.Stopped) != 0)
					throw new ThreadStateException ("Thread is dead; state can not be accessed.");

				return (ApartmentState)apartment_state;
			}

			set	{
#if NET_2_0
				TrySetApartmentState (value);
#else
				if ((ThreadState & ThreadState.Unstarted) == 0)
					throw new ThreadStateException ("Thread was in an invalid state for the operation being executed.");

				if (value != ApartmentState.STA && value != ApartmentState.MTA)
					throw new ArgumentException ("value is not a valid apartment state.");

				if ((ApartmentState)apartment_state == ApartmentState.Unknown)
					apartment_state = (byte)value;
#endif
			}
		}

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private static extern int current_lcid ();

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern CultureInfo GetCachedCurrentCulture ();

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern byte[] GetSerializedCurrentCulture ();

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern void SetCachedCurrentCulture (CultureInfo culture);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern void SetSerializedCurrentCulture (byte[] culture);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern CultureInfo GetCachedCurrentUICulture ();

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern byte[] GetSerializedCurrentUICulture ();

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern void SetCachedCurrentUICulture (CultureInfo culture);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern void SetSerializedCurrentUICulture (byte[] culture);

		/* If the current_lcid() isn't known by CultureInfo,
		 * it will throw an exception which may cause
		 * String.Concat to try and recursively look up the
		 * CurrentCulture, which will throw an exception, etc.
		 * Use a boolean to short-circuit this scenario.
		 */
		private static bool in_currentculture=false;

		static object culture_lock = new object ();
		
		/*
		 * Thread objects are shared between appdomains, and CurrentCulture
		 * should always return an object in the calling appdomain. See bug
		 * http://bugzilla.ximian.com/show_bug.cgi?id=50049 for more info.
		 * This is hard to implement correctly and efficiently, so the current
		 * implementation is not perfect: changes made in one appdomain to the 
		 * state of the current cultureinfo object are not visible to other 
		 * appdomains.
		 */		
		public CultureInfo CurrentCulture {
			get {
				if (in_currentculture)
					/* Bail out */
					return CultureInfo.InvariantCulture;

				CultureInfo culture = GetCachedCurrentCulture ();
				if (culture != null)
					return culture;

				byte[] arr = GetSerializedCurrentCulture ();
				if (arr == null) {
					lock (culture_lock) {
						in_currentculture=true;
						culture = CultureInfo.ConstructCurrentCulture ();
						//
						// Don't serialize the culture in this case to avoid
						// initializing the serialization infrastructure in the
						// common case when the culture is not set explicitly.
						//
						SetCachedCurrentCulture (culture);
						in_currentculture = false;
						if (_numberFormatter != null)
							_numberFormatter.CurrentCulture = culture;
						return culture;
					}
				}

				/*
				 * No cultureinfo object exists for this domain, so create one
				 * by deserializing the serialized form.
				 */
				in_currentculture = true;
				try {
					BinaryFormatter bf = new BinaryFormatter ();
					MemoryStream ms = new MemoryStream (arr);
					culture = (CultureInfo)bf.Deserialize (ms);
					SetCachedCurrentCulture (culture);
				} finally {
					in_currentculture = false;
				}

				if (_numberFormatter != null)
					_numberFormatter.CurrentCulture = culture;
				return culture;
			}
			
			[SecurityPermission (SecurityAction.Demand, ControlThread=true)]
			set {
				if (value == null)
					throw new ArgumentNullException ("value");

				CultureInfo culture = GetCachedCurrentCulture ();
				if (culture == value)
					return;

				value.CheckNeutral ();
				in_currentculture = true;
				try {
					SetCachedCurrentCulture (value);

					byte[] serialized_form = null;

					if (value.IsReadOnly && value.cached_serialized_form != null) {
						serialized_form = value.cached_serialized_form;
					} else {
						BinaryFormatter bf = new BinaryFormatter();
						MemoryStream ms = new MemoryStream ();
						bf.Serialize (ms, value);

						serialized_form = ms.GetBuffer ();
						if (value.IsReadOnly)
							value.cached_serialized_form = serialized_form;
					}
						
					SetSerializedCurrentCulture (serialized_form);
				} finally {
					in_currentculture = false;
				}
				if (_numberFormatter != null)
					_numberFormatter.CurrentCulture = value;
			}
		}

		public CultureInfo CurrentUICulture {
			get {
				if (in_currentculture)
					/* Bail out */
					return CultureInfo.InvariantCulture;

				CultureInfo culture = GetCachedCurrentUICulture ();
				if (culture != null)
					return culture;

				byte[] arr = GetSerializedCurrentUICulture ();
				if (arr == null) {
					lock (culture_lock) {
						in_currentculture=true;
						/* We don't
						 * distinguish
						 * between
						 * System and
						 * UI cultures
						 */
						culture = CultureInfo.ConstructCurrentUICulture ();
						//
						// Don't serialize the culture in this case to avoid
						// initializing the serialization infrastructure in the
						// common case when the culture is not set explicitly.
						//
						SetCachedCurrentUICulture (culture);
						in_currentculture = false;
						return culture;
					}
				}

				/*
				 * No cultureinfo object exists for this domain, so create one
				 * by deserializing the serialized form.
				 */
				in_currentculture = true;
				try {
					BinaryFormatter bf = new BinaryFormatter ();
					MemoryStream ms = new MemoryStream (arr);
					culture = (CultureInfo)bf.Deserialize (ms);
					SetCachedCurrentUICulture (culture);
				}
				finally {
					in_currentculture = false;
				}

				return culture;
			}
			
			set {
				in_currentculture = true;
				
				if (value == null)
					throw new ArgumentNullException ("value");

				CultureInfo culture = GetCachedCurrentUICulture ();
				if (culture == value)
					return;

				try {
					SetCachedCurrentUICulture (value);

					byte[] serialized_form = null;

					if (value.IsReadOnly && value.cached_serialized_form != null) {
						serialized_form = value.cached_serialized_form;
					} else {
						BinaryFormatter bf = new BinaryFormatter();
						MemoryStream ms = new MemoryStream ();
						bf.Serialize (ms, value);

						serialized_form = ms.GetBuffer ();
						if (value.IsReadOnly)
							value.cached_serialized_form = serialized_form;
					}
						
					SetSerializedCurrentUICulture (serialized_form);
				} finally {
					in_currentculture = false;
				}
			}
		}

		public bool IsThreadPoolThread {
			get {
				return IsThreadPoolThreadInternal;
			}
		}

		internal bool IsThreadPoolThreadInternal {
			get {
				return threadpool_thread;
			}
			set {
				threadpool_thread = value;
			}
		}

		public bool IsAlive {
			get {
				ThreadState curstate = GetState ();
				
				if((curstate & ThreadState.Aborted) != 0 ||
				   (curstate & ThreadState.Stopped) != 0 ||
				   (curstate & ThreadState.Unstarted) != 0) {
					return(false);
				} else {
					return(true);
				}
			}
		}

		public bool IsBackground {
			get {
				ThreadState thread_state = GetState ();
				if ((thread_state & ThreadState.Stopped) != 0)
					throw new ThreadStateException ("Thread is dead; state can not be accessed.");

				return (thread_state & ThreadState.Background) != 0;
			}
			
			set {
				if (value) {
					SetState (ThreadState.Background);
				} else {
					ClrState (ThreadState.Background);
				}
			}
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern string GetName_internal ();

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern void SetName_internal (String name);

		/* 
		 * The thread name must be shared by appdomains, so it is stored in
		 * unmanaged code.
		 */

		public string Name {
			get {
				return GetName_internal ();
			}
			
			set {
				SetName_internal (value);
			}
		}

		public ThreadPriority Priority {
			get {
				return(ThreadPriority.Lowest);
			}
			
			set {
				// FIXME: Implement setter.
			}
		}

		public ThreadState ThreadState {
			get {
				return GetState ();
			}
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern void Abort_internal (object stateInfo);

		[SecurityPermission (SecurityAction.Demand, ControlThread=true)]
		public void Abort () 
		{
			Abort_internal (null);
		}

		[SecurityPermission (SecurityAction.Demand, ControlThread=true)]
		public void Abort (object stateInfo) 
		{
			Abort_internal (stateInfo);
		}
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern void Interrupt_internal ();
		
		[SecurityPermission (SecurityAction.Demand, ControlThread=true)]
		public void Interrupt ()
		{
			Interrupt_internal ();
		}

		// The current thread joins with 'this'. Set ms to 0 to block
		// until this actually exits.
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern bool Join_internal(int ms, IntPtr handle);
		
		public void Join()
		{
			Join_internal(Timeout.Infinite, system_thread_handle);
		}

		public bool Join(int millisecondsTimeout)
		{
			if (millisecondsTimeout != Timeout.Infinite && millisecondsTimeout < 0)
				throw new ArgumentException ("Timeout less than zero", "millisecondsTimeout");

			return Join_internal(millisecondsTimeout, system_thread_handle);
		}

		public bool Join(TimeSpan timeout)
		{
			// LAMESPEC: says to throw ArgumentException too
			int ms=Convert.ToInt32(timeout.TotalMilliseconds);
			
			if(ms < 0 || ms > Int32.MaxValue) {
				throw new ArgumentOutOfRangeException("timeout out of range");
			}
			return Join_internal(ms, system_thread_handle);
		}

#if NET_1_1
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		public extern static void MemoryBarrier ();
#endif
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern void Resume_internal();

#if NET_2_0
		[Obsolete ("")]
#endif
		[SecurityPermission (SecurityAction.Demand, ControlThread=true)]
		public void Resume () 
		{
			Resume_internal ();
		}

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		private extern static void SpinWait_internal (int iterations);


#if NET_2_0
		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.Success)]
#endif
		public static void SpinWait (int iterations) 
		{
			SpinWait_internal (iterations);
		}

		public void Start() {
			// propagate informations from the original thread to the new thread
#if NET_2_0
			if (!ExecutionContext.IsFlowSuppressed ())
				_ec = ExecutionContext.Capture ();
#else
			// before 2.0 this was only used for security (mostly CAS) so we
			// do this only if the security manager is active
			if (SecurityManager.SecurityEnabled)
				_ec = ExecutionContext.Capture ();
#endif
			if (CurrentThread._principal != null)
				_principal = CurrentThread._principal;

			// Thread_internal creates and starts the new thread, 
			if (Thread_internal(threadstart) == (IntPtr) 0)
				throw new SystemException ("Thread creation failed.");
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern void Suspend_internal();

#if NET_2_0
		[Obsolete ("")]
#endif
		[SecurityPermission (SecurityAction.Demand, ControlThread=true)]
		public void Suspend ()
		{
			Suspend_internal ();
		}

		// Closes the system thread handle
		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		private extern void Thread_free_internal(IntPtr handle);

#if NET_2_0
		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.Success)]
#endif
		~Thread() {
			// Free up the handle
			if (system_thread_handle != (IntPtr) 0)
				Thread_free_internal(system_thread_handle);
		}

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern private void SetState (ThreadState set);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern private void ClrState (ThreadState clr);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern private ThreadState GetState ();

#if NET_1_1
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static byte VolatileRead (ref byte address);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static double VolatileRead (ref double address);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static short VolatileRead (ref short address);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static int VolatileRead (ref int address);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static long VolatileRead (ref long address);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static IntPtr VolatileRead (ref IntPtr address);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static object VolatileRead (ref object address);

		[CLSCompliant(false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static sbyte VolatileRead (ref sbyte address);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static float VolatileRead (ref float address);

		[CLSCompliant (false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static ushort VolatileRead (ref ushort address);

		[CLSCompliant (false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static uint VolatileRead (ref uint address);

		[CLSCompliant (false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static ulong VolatileRead (ref ulong address);

		[CLSCompliant (false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static UIntPtr VolatileRead (ref UIntPtr address);

		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref byte address, byte value);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref double address, double value);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref short address, short value);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref int address, int value);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref long address, long value);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref IntPtr address, IntPtr value);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref object address, object value);

		[CLSCompliant(false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref sbyte address, sbyte value);
		
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref float address, float value);

		[CLSCompliant (false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref ushort address, ushort value);

		[CLSCompliant (false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref uint address, uint value);

		[CLSCompliant (false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref ulong address, ulong value);

		[CLSCompliant (false)]
		[MethodImplAttribute (MethodImplOptions.InternalCall)]
		extern public static void VolatileWrite (ref UIntPtr address, UIntPtr value);
		
#endif

#if NET_2_0
		public Thread (ThreadStart start, int maxStackSize)
		{
			if (start == null)
				throw new ArgumentNullException ("start");
			if (maxStackSize < 131072)
				throw new ArgumentException ("< 128 kb", "maxStackSize");

			threadstart = start;
			stack_size = maxStackSize;
			Thread_init ();
		}

		public Thread (ParameterizedThreadStart start)
		{
			if (start == null)
				throw new ArgumentNullException ("start");

			threadstart = start;
			Thread_init ();
		}

		public Thread (ParameterizedThreadStart start, int maxStackSize)
		{
			if (start == null)
				throw new ArgumentNullException ("start");
			if (maxStackSize < 131072)
				throw new ArgumentException ("< 128 kb", "maxStackSize");

			threadstart = start;
			stack_size = maxStackSize;
			Thread_init ();
		}

		[MonoTODO ("limited to CompressedStack support")]
		// FIXME: We share the _ec object between appdomains
		public ExecutionContext ExecutionContext {
			[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
			get {
				if (_ec == null)
					_ec = new ExecutionContext ();
				return _ec;
			}
		}

		public int ManagedThreadId {
			[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.Success)]
			get {
				if (managed_id == 0) {
					int new_managed_id = GetNewManagedId ();
					
					Interlocked.CompareExchange (ref managed_id, new_managed_id, 0);
				}
				
				return managed_id;
			}
		}

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.MayFail)]
		public static void BeginCriticalRegion ()
		{
			CurrentThread.critical_region_level++;
		}

		[ReliabilityContract (Consistency.WillNotCorruptState, Cer.Success)]
		public static void EndCriticalRegion ()
		{
			CurrentThread.critical_region_level--;
		}

		[MonoTODO("Not implemented")]
		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public static void BeginThreadAffinity ()
		{
			throw new NotImplementedException ();
		}

		[MonoTODO("Not implemented")]
		[ReliabilityContractAttribute (Consistency.WillNotCorruptState, Cer.MayFail)]
		public static void EndThreadAffinity ()
		{
			throw new NotImplementedException ();
		}
		
		public ApartmentState GetApartmentState ()
		{
			return (ApartmentState)apartment_state;
		}

		public void SetApartmentState (ApartmentState state)
		{
			if (!TrySetApartmentState (state))
				throw new InvalidOperationException ("Failed to set the specified COM apartment state.");
		}

		public bool TrySetApartmentState (ApartmentState state) 
		{
			if ((ThreadState & ThreadState.Unstarted) == 0)
				throw new ThreadStateException ("Thread was in an invalid state for the operation being executed.");

			if ((ApartmentState)apartment_state != ApartmentState.Unknown)
				return false;

			apartment_state = (byte)state;

			return true;
		}
		
		[ComVisible (false)]
		public override int GetHashCode ()
		{
			return ManagedThreadId;
		}

		public void Start (object parameter)
		{
			start_obj = parameter;
			Start ();
		}
#else
		internal ExecutionContext ExecutionContext {
			get {
				if (_ec == null)
					_ec = new ExecutionContext ();
				return _ec;
			}
		}
#endif

		// NOTE: This method doesn't show in the class library status page because
		// it cannot be "found" with the StrongNameIdentityPermission for ECMA key.
		// But it's there!
		[SecurityPermission (SecurityAction.LinkDemand, UnmanagedCode = true)]
		[StrongNameIdentityPermission (SecurityAction.LinkDemand, PublicKey="00000000000000000400000000000000")]
#if NET_2_0
		[Obsolete ("see CompressedStack class")]
#endif
#if NET_1_1
		public
#else
		internal
#endif
		CompressedStack GetCompressedStack ()
		{
			// Note: returns null if no CompressedStack has been set.
			// However CompressedStack.GetCompressedStack returns an 
			// (empty?) CompressedStack instance.
			CompressedStack cs = ExecutionContext.SecurityContext.CompressedStack;
			return ((cs == null) || cs.IsEmpty ()) ? null : cs.CreateCopy ();
		}

		// NOTE: This method doesn't show in the class library status page because
		// it cannot be "found" with the StrongNameIdentityPermission for ECMA key.
		// But it's there!
		[SecurityPermission (SecurityAction.LinkDemand, UnmanagedCode = true)]
		[StrongNameIdentityPermission (SecurityAction.LinkDemand, PublicKey="00000000000000000400000000000000")]
#if NET_2_0
		[Obsolete ("see CompressedStack class")]
#endif
#if NET_1_1
		public
#else
		internal
#endif
		void SetCompressedStack (CompressedStack stack)
		{
			ExecutionContext.SecurityContext.CompressedStack = stack;
		}

#if NET_1_1
		void _Thread.GetIDsOfNames ([In] ref Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgDispId)
		{
			throw new NotImplementedException ();
		}

		void _Thread.GetTypeInfo (uint iTInfo, uint lcid, IntPtr ppTInfo)
		{
			throw new NotImplementedException ();
		}

		void _Thread.GetTypeInfoCount (out uint pcTInfo)
		{
			throw new NotImplementedException ();
		}

		void _Thread.Invoke (uint dispIdMember, [In] ref Guid riid, uint lcid, short wFlags, IntPtr pDispParams,
			IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr)
		{
			throw new NotImplementedException ();
		}
#endif
	}
}
