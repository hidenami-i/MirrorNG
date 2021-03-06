using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using PropertyAttributes = Mono.Cecil.PropertyAttributes;

namespace Mirror.Weaver
{
    /// <summary>
    /// Processes [SyncVar] in NetworkBehaviour
    /// </summary>
    public class SyncVarProcessor
    {
        private readonly List<FieldDefinition> syncVars = new List<FieldDefinition>();
        // <SyncVarField,NetIdField>
        private readonly Dictionary<FieldDefinition, FieldDefinition> syncVarNetIds = new Dictionary<FieldDefinition, FieldDefinition>();

        // ulong = 64 bytes
        const int SyncVarLimit = 64;


        static string HookParameterMessage(string hookName, TypeReference ValueType)
            => string.Format("void {0}({1} oldValue, {1} newValue)", hookName, ValueType);

        // Get hook method if any
        static MethodDefinition GetHookMethod(FieldDefinition syncVar)
        {
            CustomAttribute syncVarAttr = syncVar.GetCustomAttribute<SyncVarAttribute>();

            if (syncVarAttr == null)
                return null;

            string hookFunctionName = syncVarAttr.GetField<string>("hook", null);

            if (hookFunctionName == null)
                return null;

            return FindHookMethod(syncVar, hookFunctionName);
        }

        static MethodDefinition FindHookMethod(FieldDefinition syncVar, string hookFunctionName)
        {
            List<MethodDefinition> methods = syncVar.DeclaringType.GetMethods(hookFunctionName);

            var methodsWith2Param = new List<MethodDefinition>(methods.Where(m => m.Parameters.Count == 2));

            if (methodsWith2Param.Count == 0)
            {
                Weaver.Error($"Could not find hook for '{syncVar.Name}', hook name '{hookFunctionName}'. " +
                    $"Method signature should be {HookParameterMessage(hookFunctionName, syncVar.FieldType)}",
                    syncVar);

                return null;
            }

            foreach (MethodDefinition method in methodsWith2Param)
            {
                if (MatchesParameters(syncVar, method))
                {
                    return method;
                }
            }

            Weaver.Error($"Wrong type for Parameter in hook for '{syncVar.Name}', hook name '{hookFunctionName}'. " +
                     $"Method signature should be {HookParameterMessage(hookFunctionName, syncVar.FieldType)}",
                   syncVar);

            return null;
        }

        static bool MatchesParameters(FieldDefinition syncVar, MethodDefinition method)
        {
            // matches void onValueChange(T oldValue, T newValue)
            return method.Parameters[0].ParameterType.FullName == syncVar.FieldType.FullName &&
                   method.Parameters[1].ParameterType.FullName == syncVar.FieldType.FullName;
        }

        static MethodDefinition GenerateSyncVarGetter(FieldDefinition fd, string originalName, FieldDefinition netFieldId)
        {
            //Create the get method
            MethodDefinition get = fd.DeclaringType.AddMethod(
                    "get_Network" + originalName, MethodAttributes.Public |
                    MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig,
                    fd.FieldType);

            ILProcessor worker = get.Body.GetILProcessor();

            // [SyncVar] NetworkIdentity?
            if (fd.FieldType.Is<NetworkIdentity>())
            {
                // return this.GetSyncVarNetworkIdentity(netId, field);
                // this.
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, netFieldId));
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, fd));
                worker.Append(worker.Create<NetworkBehaviour>(OpCodes.Call, nb => nb.GetSyncVarNetworkIdentity(default, default)));
                worker.Append(worker.Create(OpCodes.Ret));
            }
            // [SyncVar] int, string, etc.
            else
            {
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, fd));
                worker.Append(worker.Create(OpCodes.Ret));
            }

            get.SemanticsAttributes = MethodSemanticsAttributes.Getter;

            return get;
        }

        static MethodDefinition GenerateSyncVarSetter(FieldDefinition fd, string originalName, long dirtyBit, FieldDefinition netFieldId)
        {
            //Create the set method
            MethodDefinition set = fd.DeclaringType.AddMethod("set_Network" + originalName, MethodAttributes.Public |
                    MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig);
            ParameterDefinition valueParam = set.AddParam(fd.FieldType, "value");
            set.SemanticsAttributes = MethodSemanticsAttributes.Setter;

            ILProcessor worker = set.Body.GetILProcessor();

            // if (!SyncVarEqual(value, ref playerData))
            Instruction endOfMethod = worker.Create(OpCodes.Nop);

            // this
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            // new value to set
            worker.Append(worker.Create(OpCodes.Ldarg, valueParam));
            // reference to field to set
            // make generic version of SetSyncVar with field type
            if (fd.FieldType.Is<NetworkIdentity>())
            {
                // reference to netId Field to set
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, netFieldId));

                worker.Append(worker.Create<NetworkBehaviour>(OpCodes.Call, nb => nb.SyncVarNetworkIdentityEqual(default, default)));
            }
            else
            {
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, fd));
                MethodReference syncVarEqual = fd.Module.ImportReference<NetworkBehaviour>(nb => nb.SyncVarEqual<object>(default, default));
                var syncVarEqualGm = new GenericInstanceMethod(syncVarEqual.GetElementMethod());
                syncVarEqualGm.GenericArguments.Add(fd.FieldType);
                worker.Append(worker.Create(OpCodes.Call, syncVarEqualGm));
            }

            worker.Append(worker.Create(OpCodes.Brtrue, endOfMethod));

            // T oldValue = value;
            VariableDefinition oldValue = set.AddLocal(fd.FieldType);
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldfld, fd));
            worker.Append(worker.Create(OpCodes.Stloc, oldValue));

            if (fd.FieldType.Is<NetworkIdentity>())
            {
                // this
                worker.Append(worker.Create(OpCodes.Ldarg_0));

                // new value to set
                worker.Append(worker.Create(OpCodes.Ldarg, valueParam));

                // reference to field to set
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldflda, fd));

                // dirty bit
                // 8 byte integer aka long
                worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));

                // reference to netId Field to set
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldflda, netFieldId));

                NetworkIdentity ni = default;
                uint netIdField = default;
                worker.Append(worker.Create<NetworkBehaviour>(OpCodes.Call, nb => nb.SetSyncVarNetworkIdentity(default,ref ni, default, ref netIdField )));
            }
            else
            {
                // fieldValue = value;
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldarg, valueParam));
                worker.Append(worker.Create(OpCodes.Stfld, fd));

                // this.SetDirtyBit(dirtyBit)
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));
                worker.Append(worker.Create<NetworkBehaviour>(OpCodes.Call, nb => nb.SetDirtyBit(default)));
            }

            MethodDefinition hookMethod = GetHookMethod(fd);

            if (hookMethod != null)
            {
                //if (base.isLocalClient && !getSyncVarHookGuard(dirtyBit))
                Instruction label = worker.Create(OpCodes.Nop);
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Call, (NetworkBehaviour nb) => nb.IsLocalClient));
                worker.Append(worker.Create(OpCodes.Brfalse, label));
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));
                worker.Append(worker.Create<NetworkBehaviour>(OpCodes.Call, nb => nb.GetSyncVarHookGuard(default)));
                worker.Append(worker.Create(OpCodes.Brtrue, label));

                // setSyncVarHookGuard(dirtyBit, true);
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));
                worker.Append(worker.Create(OpCodes.Ldc_I4_1));
                worker.Append(worker.Create<NetworkBehaviour>(OpCodes.Call, nb => nb.SetSyncVarHookGuard(default, default)));

                // call hook (oldValue, newValue)
                // Generates: OnValueChanged(oldValue, value);
                WriteCallHookMethodUsingArgument(worker, hookMethod, oldValue);

                // setSyncVarHookGuard(dirtyBit, false);
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));
                worker.Append(worker.Create(OpCodes.Ldc_I4_0));
                worker.Append(worker.Create<NetworkBehaviour>(OpCodes.Call, nb => nb.SetSyncVarHookGuard(default, default)));

                worker.Append(label);
            }

            worker.Append(endOfMethod);

            worker.Append(worker.Create(OpCodes.Ret));


            return set;
        }

        static void ProcessSyncVar(FieldDefinition fd, Dictionary<FieldDefinition, FieldDefinition> syncVarNetIds, long dirtyBit)
        {
            string originalName = fd.Name;
            Weaver.DLog(fd.DeclaringType, "Sync Var " + fd.Name + " " + fd.FieldType);

            // NetworkIdentity SyncVars have a new field for netId
            FieldDefinition netIdField = null;
            if (fd.FieldType.Is<NetworkIdentity>())
            {
                netIdField = new FieldDefinition("___" + fd.Name + "NetId",
                    FieldAttributes.Private,
                    fd.Module.ImportReference<uint>());

                fd.DeclaringType.Fields.Add(netIdField);
                syncVarNetIds[fd] = netIdField;
            }

            MethodDefinition get = GenerateSyncVarGetter(fd, originalName, netIdField);
            MethodDefinition set = GenerateSyncVarSetter(fd, originalName, dirtyBit, netIdField);

            //NOTE: is property even needed? Could just use a setter function?
            //create the property
            var propertyDefinition = new PropertyDefinition("Network" + originalName, PropertyAttributes.None, fd.FieldType)
            {
                GetMethod = get,
                SetMethod = set
            };

            propertyDefinition.DeclaringType = fd.DeclaringType;
            //add the methods and property to the type.
            fd.DeclaringType.Properties.Add(propertyDefinition);
            Weaver.WeaveLists.replacementSetterProperties[fd] = set;

            // replace getter field if NetworkIdentity so it uses
            // netId instead
            // -> only for GameObjects, otherwise an int syncvar's getter would
            //    end up in recursion.
            if (fd.FieldType.Is<NetworkIdentity>())
            {
                Weaver.WeaveLists.replacementGetterProperties[fd] = get;
            }
        }

        public void ProcessSyncVars(TypeDefinition td)
        {
            // the mapping of dirtybits to sync-vars is implicit in the order of the fields here. this order is recorded in m_replacementProperties.
            // start assigning syncvars at the place the base class stopped, if any
            int dirtyBitCounter = Weaver.WeaveLists.GetSyncVarStart(td.BaseType.FullName);

            var fields = new List<FieldDefinition>(td.Fields);

            // find syncvars
            foreach (FieldDefinition fd in fields)
            {
                if (!fd.HasCustomAttribute<SyncVarAttribute>())
                {
                    continue;
                }

                if ((fd.Attributes & FieldAttributes.Static) != 0)
                {
                    Weaver.Error($"{fd.Name} cannot be static", fd);
                    continue;
                }

                if (fd.FieldType.IsArray)
                {
                    Weaver.Error($"{fd.Name} has invalid type. Use SyncLists instead of arrays", fd);
                    continue;
                }

                if (SyncObjectProcessor.ImplementsSyncObject(fd.FieldType))
                {
                    Weaver.Warning($"{fd.Name} has [SyncVar] attribute. SyncLists should not be marked with SyncVar", fd);
                    continue;
                }
                syncVars.Add(fd);

                ProcessSyncVar(fd, syncVarNetIds, 1L << dirtyBitCounter);
                dirtyBitCounter += 1;
            }

            if (dirtyBitCounter >= SyncVarLimit)
            {
                Weaver.Error($"{td.Name} has too many SyncVars. Consider refactoring your class into multiple components", td);
            }

            Weaver.WeaveLists.SetNumSyncVars(td.FullName, syncVars.Count);

            GenerateSerialization(td);
            GenerateDeSerialization(td);
        }

        static void WriteCallHookMethodUsingArgument(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue)
        {
            WriteCallHookMethod(worker, hookMethod, oldValue, null);
        }

        static void WriteCallHookMethodUsingField(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue, FieldDefinition newValue)
        {
            if (newValue == null)
            {
                Weaver.Error("NewValue field was null when writing SyncVar hook");
            }

            WriteCallHookMethod(worker, hookMethod, oldValue, newValue);
        }

        static void WriteCallHookMethod(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue, FieldDefinition newValue)
        {
            WriteStartFunctionCall();

            // write args
            WriteOldValue();
            WriteNewValue();

            WriteEndFunctionCall();


            // *** Local functions used to write OpCodes ***
            // Local functions have access to function variables, no need to pass in args

            void WriteOldValue()
            {
                worker.Append(worker.Create(OpCodes.Ldloc, oldValue));
            }

            void WriteNewValue()
            {
                // write arg1 or this.field
                if (newValue == null)
                {
                    worker.Append(worker.Create(OpCodes.Ldarg_1));
                }
                else
                {
                    // this.
                    worker.Append(worker.Create(OpCodes.Ldarg_0));
                    // syncvar.get
                    worker.Append(worker.Create(OpCodes.Ldfld, newValue));
                }
            }

            // Writes this before method if it is not static
            void WriteStartFunctionCall()
            {
                // dont add this (Ldarg_0) if method is static
                if (!hookMethod.IsStatic)
                {
                    // this before method call
                    // eg this.onValueChanged
                    worker.Append(worker.Create(OpCodes.Ldarg_0));
                }
            }

            // Calls method
            void WriteEndFunctionCall()
            {
                // only use Callvirt when not static
                OpCode opcode = hookMethod.IsStatic ? OpCodes.Call : OpCodes.Callvirt;
                worker.Append(worker.Create(opcode, hookMethod));
            }
        }

        void GenerateSerialization(TypeDefinition netBehaviourSubclass)
        {
            Weaver.DLog(netBehaviourSubclass, "  GenerateSerialization");

            const string SerializeMethodName = nameof(NetworkBehaviour.SerializeSyncVars);
            if (netBehaviourSubclass.GetMethod(SerializeMethodName) != null)
                return;

            if (syncVars.Count == 0)
            {
                // no synvars,  no need for custom OnSerialize
                return;
            }

            MethodDefinition serialize = netBehaviourSubclass.AddMethod(SerializeMethodName,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    netBehaviourSubclass.Module.ImportReference<bool>());

            ParameterDefinition writerParameter = serialize.AddParam<NetworkWriter>("writer");
            ParameterDefinition initializeParameter = serialize.AddParam<bool>("initialize");
            ILProcessor worker = serialize.Body.GetILProcessor();

            serialize.Body.InitLocals = true;

            // loc_0,  this local variable is to determine if any variable was dirty
            VariableDefinition dirtyLocal = serialize.AddLocal<bool>();

            MethodDefinition baseSerialize = netBehaviourSubclass.BaseType.Resolve().GetMethodInBaseType(SerializeMethodName);
            if (baseSerialize != null)
            {
                // base
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                // writer
                worker.Append(worker.Create(OpCodes.Ldarg, writerParameter));
                // forceAll
                worker.Append(worker.Create(OpCodes.Ldarg, initializeParameter));
                worker.Append(worker.Create(OpCodes.Call, netBehaviourSubclass.Module.ImportReference(baseSerialize)));
                // set dirtyLocal to result of base.OnSerialize()
                worker.Append(worker.Create(OpCodes.Stloc, dirtyLocal));
            }

            // Generates: if (forceAll);
            Instruction initialStateLabel = worker.Create(OpCodes.Nop);
            // forceAll
            worker.Append(worker.Create(OpCodes.Ldarg, initializeParameter));
            worker.Append(worker.Create(OpCodes.Brfalse, initialStateLabel));

            foreach (FieldDefinition syncVar in syncVars)
            {
                // Generates a writer call for each sync variable
                // writer
                worker.Append(worker.Create(OpCodes.Ldarg, writerParameter));
                // this
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, syncVar));
                MethodReference writeFunc = netBehaviourSubclass.Module.GetWriteFunc(syncVar.FieldType);
                if (writeFunc != null)
                {
                    worker.Append(worker.Create(OpCodes.Call, writeFunc));
                }
                else
                {
                    Weaver.Error($"{syncVar.Name} has unsupported type. Use a supported MirrorNG type instead", syncVar);
                    return;
                }
            }

            // always return true if forceAll

            // Generates: return true
            worker.Append(worker.Create(OpCodes.Ldc_I4_1));
            worker.Append(worker.Create(OpCodes.Ret));

            // Generates: end if (forceAll);
            worker.Append(initialStateLabel);

            // write dirty bits before the data fields
            // Generates: writer.WritePackedUInt64 (base.get_syncVarDirtyBits ());
            // writer
            worker.Append(worker.Create(OpCodes.Ldarg, writerParameter));
            // base
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Call, (NetworkBehaviour nb) => nb.SyncVarDirtyBits));
            MethodReference writeUint64Func = netBehaviourSubclass.Module.GetWriteFunc<ulong>();
            worker.Append(worker.Create(OpCodes.Call, writeUint64Func));

            // generate a writer call for any dirty variable in this class

            // start at number of syncvars in parent
            int dirtyBit = Weaver.WeaveLists.GetSyncVarStart(netBehaviourSubclass.BaseType.FullName);
            foreach (FieldDefinition syncVar in syncVars)
            {
                Instruction varLabel = worker.Create(OpCodes.Nop);

                // Generates: if ((base.get_syncVarDirtyBits() & 1uL) != 0uL)
                // base
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Call, (NetworkBehaviour nb) => nb.SyncVarDirtyBits));
                // 8 bytes = long
                worker.Append(worker.Create(OpCodes.Ldc_I8, 1L << dirtyBit));
                worker.Append(worker.Create(OpCodes.And));
                worker.Append(worker.Create(OpCodes.Brfalse, varLabel));

                // Generates a call to the writer for that field
                // writer
                worker.Append(worker.Create(OpCodes.Ldarg, writerParameter));
                // base
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, syncVar));

                MethodReference writeFunc = netBehaviourSubclass.Module.GetWriteFunc(syncVar.FieldType);
                if (writeFunc != null)
                {
                    worker.Append(worker.Create(OpCodes.Call, writeFunc));
                }
                else
                {
                    Weaver.Error($"{syncVar.Name} has unsupported type. Use a supported MirrorNG type instead", syncVar);
                    return;
                }

                // something was dirty
                worker.Append(worker.Create(OpCodes.Ldc_I4_1));
                // set dirtyLocal to true
                worker.Append(worker.Create(OpCodes.Stloc, dirtyLocal));

                worker.Append(varLabel);
                dirtyBit += 1;
            }

            // generate: return dirtyLocal
            worker.Append(worker.Create(OpCodes.Ldloc, dirtyLocal));
            worker.Append(worker.Create(OpCodes.Ret));
        }


        /// <summary>
        /// Is the field a NetworkIdentity or GameObject
        /// </summary>
        /// <param name="syncVar"></param>
        /// <returns></returns>
        static bool IsNetworkIdentityField(FieldDefinition syncVar)
        {
            return syncVar.FieldType.Is<UnityEngine.GameObject>() ||
                   syncVar.FieldType.Is<NetworkIdentity>();
        }


        void DeserializeField(FieldDefinition syncVar, ILProcessor worker, MethodDefinition deserialize)
        {
            // check for Hook function
            MethodDefinition hookMethod = GetHookMethod(syncVar);

            if (IsNetworkIdentityField(syncVar))
            {
                DeserializeNetworkIdentityField(syncVar, worker, deserialize, hookMethod);
            }
            else
            {
                DeserializeNormalField(syncVar, worker, deserialize, hookMethod);
            }
        }

        void GenerateDeSerialization(TypeDefinition netBehaviourSubclass)
        {
            Weaver.DLog(netBehaviourSubclass, "  GenerateDeSerialization");

            const string DeserializeMethodName = nameof(NetworkBehaviour.DeserializeSyncVars);
            if (netBehaviourSubclass.GetMethod(DeserializeMethodName) != null)
                return;

            if (syncVars.Count == 0)
            {
                // no synvars,  no need for custom OnDeserialize
                return;
            }

            MethodDefinition serialize = netBehaviourSubclass.AddMethod(DeserializeMethodName,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig);

            ParameterDefinition readerParam = serialize.AddParam<NetworkReader>("reader");
            ParameterDefinition initializeParam = serialize.AddParam<bool>("initialState");
            ILProcessor serWorker = serialize.Body.GetILProcessor();
            // setup local for dirty bits
            serialize.Body.InitLocals = true;
            VariableDefinition dirtyBitsLocal = serialize.AddLocal<long>();

            MethodDefinition baseDeserialize = netBehaviourSubclass.BaseType.Resolve().GetMethodInBaseType(DeserializeMethodName);
            if (baseDeserialize != null)
            {
                // base
                serWorker.Append(serWorker.Create(OpCodes.Ldarg_0));
                // reader
                serWorker.Append(serWorker.Create(OpCodes.Ldarg,readerParam));
                // initialState
                serWorker.Append(serWorker.Create(OpCodes.Ldarg,initializeParam));
                serWorker.Append(serWorker.Create(OpCodes.Call, netBehaviourSubclass.Module.ImportReference(baseDeserialize)));
            }

            // Generates: if (initialState);
            Instruction initialStateLabel = serWorker.Create(OpCodes.Nop);

            serWorker.Append(serWorker.Create(OpCodes.Ldarg, initializeParam));
            serWorker.Append(serWorker.Create(OpCodes.Brfalse, initialStateLabel));

            foreach (FieldDefinition syncVar in syncVars)
            {
                DeserializeField(syncVar, serWorker, serialize);
            }

            serWorker.Append(serWorker.Create(OpCodes.Ret));

            // Generates: end if (initialState);
            serWorker.Append(initialStateLabel);

            // get dirty bits
            serWorker.Append(serWorker.Create(OpCodes.Ldarg, readerParam));
            serWorker.Append(serWorker.Create(OpCodes.Call, netBehaviourSubclass.Module.GetReadFunc<ulong>()));
            serWorker.Append(serWorker.Create(OpCodes.Stloc, dirtyBitsLocal));

            // conditionally read each syncvar
            // start at number of syncvars in parent
            int dirtyBit = Weaver.WeaveLists.GetSyncVarStart(netBehaviourSubclass.BaseType.FullName);
            foreach (FieldDefinition syncVar in syncVars)
            {
                Instruction varLabel = serWorker.Create(OpCodes.Nop);

                // check if dirty bit is set
                serWorker.Append(serWorker.Create(OpCodes.Ldloc, dirtyBitsLocal));
                serWorker.Append(serWorker.Create(OpCodes.Ldc_I8, 1L << dirtyBit));
                serWorker.Append(serWorker.Create(OpCodes.And));
                serWorker.Append(serWorker.Create(OpCodes.Brfalse, varLabel));

                DeserializeField(syncVar, serWorker, serialize);

                serWorker.Append(varLabel);
                dirtyBit += 1;
            }

            serWorker.Append(serWorker.Create(OpCodes.Ret));
        }

        /// <summary>
        /// [SyncVar] int/float/struct/etc.?
        /// </summary>
        /// <param name="syncVar"></param>
        /// <param name="serWorker"></param>
        /// <param name="deserialize"></param>
        /// <param name="initialState"></param>
        /// <param name="hookResult"></param>
        void DeserializeNormalField(FieldDefinition syncVar, ILProcessor serWorker, MethodDefinition deserialize, MethodDefinition hookMethod)
        {
            /*
             Generates code like:
                // for hook
                int oldValue = a;
                Networka = reader.ReadPackedInt32();
                if (!SyncVarEqual(oldValue, ref a))
                {
                    OnSetA(oldValue, Networka);
                }
             */
            ModuleDefinition module = serWorker.Body.Method.Module;
            MethodReference readFunc = module.GetReadFunc(syncVar.FieldType);
            if (readFunc == null)
            {
                Weaver.Error($"{syncVar.Name} has unsupported type. Use a supported MirrorNG type instead", syncVar);
                return;
            }

            // T oldValue = value;
            VariableDefinition oldValue = deserialize.AddLocal(syncVar.FieldType);
            serWorker.Append(serWorker.Create(OpCodes.Ldarg_0));
            serWorker.Append(serWorker.Create(OpCodes.Ldfld, syncVar));
            serWorker.Append(serWorker.Create(OpCodes.Stloc, oldValue));

            // read value and store in syncvar BEFORE calling the hook
            // -> this makes way more sense. by definition, the hook is
            //    supposed to be called after it was changed. not before.
            // -> setting it BEFORE calling the hook fixes the following bug:
            //    https://github.com/vis2k/Mirror/issues/1151 in host mode
            //    where the value during the Hook call would call Cmds on
            //    the host server, and they would all happen and compare
            //    values BEFORE the hook even returned and hence BEFORE the
            //    actual value was even set.
            // put 'this.' onto stack for 'this.syncvar' below
            serWorker.Append(serWorker.Create(OpCodes.Ldarg_0));
            // reader. for 'reader.Read()' below
            serWorker.Append(serWorker.Create(OpCodes.Ldarg_1));
            // reader.Read()
            serWorker.Append(serWorker.Create(OpCodes.Call, readFunc));
            // syncvar
            serWorker.Append(serWorker.Create(OpCodes.Stfld, syncVar));

            if (hookMethod != null)
            {
                // call hook
                // but only if SyncVar changed. otherwise a client would
                // get hook calls for all initial values, even if they
                // didn't change from the default values on the client.
                // see also: https://github.com/vis2k/Mirror/issues/1278

                // Generates: if (!SyncVarEqual);
                Instruction syncVarEqualLabel = serWorker.Create(OpCodes.Nop);

                // 'this.' for 'this.SyncVarEqual'
                serWorker.Append(serWorker.Create(OpCodes.Ldarg_0));
                // 'oldValue'
                serWorker.Append(serWorker.Create(OpCodes.Ldloc, oldValue));
                // 'ref this.syncVar'
                serWorker.Append(serWorker.Create(OpCodes.Ldarg_0));
                serWorker.Append(serWorker.Create(OpCodes.Ldfld, syncVar));
                // call the function
                MethodReference syncVarEqual = syncVar.Module.ImportReference<NetworkBehaviour>(nb => nb.SyncVarEqual<object>(default, default));
                var syncVarEqualGm = new GenericInstanceMethod(syncVarEqual.GetElementMethod());
                syncVarEqualGm.GenericArguments.Add(syncVar.FieldType);
                serWorker.Append(serWorker.Create(OpCodes.Call, syncVarEqualGm));
                serWorker.Append(serWorker.Create(OpCodes.Brtrue, syncVarEqualLabel));

                // call the hook
                // Generates: OnValueChanged(oldValue, this.syncVar);
                WriteCallHookMethodUsingField(serWorker, hookMethod, oldValue, syncVar);

                // Generates: end if (!SyncVarEqual);
                serWorker.Append(syncVarEqualLabel);
            }
        }


        /// <summary>
        /// [SyncVar] GameObject/NetworkIdentity?
        /// </summary>
        /// <param name="syncVar"></param>
        /// <param name="worker"></param>
        /// <param name="deserialize"></param>
        /// <param name="initialState"></param>
        /// <param name="hookResult"></param>
        void DeserializeNetworkIdentityField(FieldDefinition syncVar, ILProcessor worker, MethodDefinition deserialize, MethodDefinition hookMethod)
        {
            /*
            Generates code like:
               uint oldNetId = ___qNetId;
               // returns GetSyncVarGameObject(___qNetId)
               GameObject oldSyncVar = syncvar.getter;
               ___qNetId = reader.ReadPackedUInt32();
               if (!SyncVarEqual(oldNetId, ref ___goNetId))
               {
                   // getter returns GetSyncVarGameObject(___qNetId)
                   OnSetQ(oldSyncVar, syncvar.getter);
               }
            */

            // GameObject/NetworkIdentity SyncVar:
            //   OnSerialize sends writer.Write(go);
            //   OnDeserialize reads to __netId manually so we can use
            //     lookups in the getter (so it still works if objects
            //     move in and out of range repeatedly)
            FieldDefinition netIdField = syncVarNetIds[syncVar];

            // uint oldNetId = ___qNetId;
            VariableDefinition oldNetId = deserialize.AddLocal<uint>();
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldfld, netIdField));
            worker.Append(worker.Create(OpCodes.Stloc, oldNetId));

            // GameObject/NetworkIdentity oldSyncVar = syncvar.getter;
            VariableDefinition oldSyncVar = deserialize.AddLocal(syncVar.FieldType);
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldfld, syncVar));
            worker.Append(worker.Create(OpCodes.Stloc, oldSyncVar));

            // read id and store in netId field BEFORE calling the hook
            // -> this makes way more sense. by definition, the hook is
            //    supposed to be called after it was changed. not before.
            // -> setting it BEFORE calling the hook fixes the following bug:
            //    https://github.com/vis2k/Mirror/issues/1151 in host mode
            //    where the value during the Hook call would call Cmds on
            //    the host server, and they would all happen and compare
            //    values BEFORE the hook even returned and hence BEFORE the
            //    actual value was even set.
            // put 'this.' onto stack for 'this.netId' below
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            // reader. for 'reader.Read()' below
            worker.Append(worker.Create(OpCodes.Ldarg_1));
            // Read()
            ModuleDefinition module = worker.Body.Method.Module;
            worker.Append(worker.Create(OpCodes.Call, module.GetReadFunc<uint>()));
            // netId
            worker.Append(worker.Create(OpCodes.Stfld, netIdField));

            if (hookMethod != null)
            {
                // call Hook(this.GetSyncVarGameObject/NetworkIdentity(reader.ReadPackedUInt32()))
                // because we send/receive the netID, not the GameObject/NetworkIdentity
                // but only if SyncVar changed. otherwise a client would
                // get hook calls for all initial values, even if they
                // didn't change from the default values on the client.
                // see also: https://github.com/vis2k/Mirror/issues/1278

                // IMPORTANT: for GameObjects/NetworkIdentities we usually
                //            use SyncVarGameObjectEqual to compare equality.
                //            in this case however, we can just use
                //            SyncVarEqual with the two uint netIds.
                //            => this is easier weaver code because we don't
                //               have to get the GameObject/NetworkIdentity
                //               from the uint netId
                //            => this is faster because we void one
                //               GetComponent call for GameObjects to get
                //               their NetworkIdentity when comparing.

                // Generates: if (!SyncVarEqual);
                Instruction syncVarEqualLabel = worker.Create(OpCodes.Nop);

                // 'this.' for 'this.SyncVarEqual'
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                // 'oldNetId'
                worker.Append(worker.Create(OpCodes.Ldloc, oldNetId));
                // 'ref this.__netId'
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, netIdField));
                MethodReference syncVarEqual = module.ImportReference<NetworkBehaviour>(nb => nb.SyncVarEqual<object>(default, default));
                var syncVarEqualGm = new GenericInstanceMethod(syncVarEqual.GetElementMethod());
                syncVarEqualGm.GenericArguments.Add(netIdField.FieldType);
                worker.Append(worker.Create(OpCodes.Call, syncVarEqualGm));
                worker.Append(worker.Create(OpCodes.Brtrue, syncVarEqualLabel));

                // call the hook
                // Generates: OnValueChanged(oldValue, this.syncVar);
                WriteCallHookMethodUsingField(worker, hookMethod, oldSyncVar, syncVar);

                // Generates: end if (!SyncVarEqual);
                worker.Append(syncVarEqualLabel);
            }
        }

    }
}
