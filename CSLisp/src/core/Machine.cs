using CSLisp.Data;
using CSLisp.Error;

namespace CSLisp.Core
{
    /// <summary>
    /// Virtual machine that will interpret compiled bytecode
    /// </summary>
    public class Machine
    {
        /// <summary> If set, instructions will be logged to this function as they're executed. </summary>
        private LoggerCallback _logger = null;

        /// <summary> Internal execution context </summary>
        private Context _ctx = null;

        public Machine (Context ctx, LoggerCallback logger) {
            _ctx = ctx;
            _logger = logger;
        }

        /// <summary> Runs the given piece of code, and returns the value left at the top of the stack. </summary>
        public Val Execute (Closure fn, params Val[] args) {
            State st = new State(fn, args);

            _logger?.Invoke(string.Format("Executing closure '{0}'", fn.name));

            while (!st.done) {
                if (st.pc >= st.code.Count) {
                    throw new LanguageError("Runaway opcodes!");
                }

                // fetch instruction
                Instruction instr = st.code[st.pc];
                _logger?.Invoke(string.Format("[{0,2}] {1,3} : {2}", st.stack.Count, st.pc, Instruction.PrintInstruction(instr)));
                st.pc++;

                // and now a big old switch statement. not handler functions - this is much faster.

                switch (instr.type) {
                    case Opcode.LABEL:
                        // no op :)
                        break;

                    case Opcode.CONST: {
                            st.stack.Push(instr.first);
                        }
                        break;

                    case Opcode.LVAR: {
                            VarPos pos = new VarPos(instr.first, instr.second);
                            Val value = Environment.GetValueAt(pos, st.env);
                            st.stack.Push(value);
                        }
                        break;

                    case Opcode.LSET: {
                            Val val = st.stack.Peek();
                            VarPos pos = new VarPos(instr.first, instr.second);
                            Environment.SetValueAt(pos, val, st.env);
                        }
                        break;

                    case Opcode.GVAR: {
                            Symbol symbol = instr.first.AsSymbol;
                            Val value = symbol.pkg.GetValue(symbol);
                            st.stack.Push(value);
                        }
                        break;

                    case Opcode.GSET: {
                            Symbol symbol = instr.first.AsSymbol;
                            Val value = st.stack.Peek();
                            symbol.pkg.SetValue(symbol, value);
                        }
                        break;

                    case Opcode.POP:
                        st.stack.Pop();
                        break;

                    case Opcode.TJUMP: {
                            Val value = st.stack.Pop();
                            if (value.CastToBool) {
                                st.pc = GetLabelPosition(instr, st);
                            }
                        }
                        break;

                    case Opcode.FJUMP: {
                            Val value = st.stack.Pop();
                            if (!value.CastToBool) {
                                st.pc = GetLabelPosition(instr, st);
                            }
                        }
                        break;

                    case Opcode.JUMP: {
                            st.pc = GetLabelPosition(instr, st);
                        }
                        break;

                    case Opcode.ARGS: {
                            int argcount = instr.first.AsInt;
                            if (st.nargs != argcount) { throw new LanguageError($"Argument count error, expected {argcount}, got {st.nargs}"); }

                            // make an environment for the given number of named args
                            st.env = new Environment(st.nargs, st.env);

                            // move named arguments onto the stack frame
                            for (int i = argcount - 1; i >= 0; i--) {
                                st.env.SetValue(i, st.stack.Pop());
                            }
                        }
                        break;

                    case Opcode.ARGSDOT: {
                            int argcount = instr.first.AsInt;
                            if (st.nargs < argcount) { throw new LanguageError($"Argument count error, expected {argcount} or more, got {st.nargs}"); }

                            // make an environment for all named args, +1 for the list of remaining varargs
                            int dotted = st.nargs - argcount;
                            st.env = new Environment(argcount + 1, st.env);

                            // cons up dotted values from the stack
                            for (int dd = dotted - 1; dd >= 0; dd--) {
                                Val arg = st.stack.Pop();
                                st.env.SetValue(argcount, new Val(new Cons(arg, st.env.GetValue(argcount))));
                            }

                            // and move the named ones onto the environment stack frame
                            for (int i = argcount - 1; i >= 0; i--) {
                                st.env.SetValue(i, st.stack.Pop());
                            }
                        }
                        break;

                    case Opcode.DUPE: {
                            if (st.stack.Count == 0) { throw new LanguageError("Cannot duplicate on an empty stack!"); }
                            st.stack.Push(st.stack.Peek());
                        }
                        break;

                    case Opcode.CALLJ: {
                            st.env = st.env.parent; // discard the top environment frame
                            Val top = st.stack.Pop();
                            Closure closure = top.AsClosureOrNull;

                            // set vm state to the beginning of the closure
                            st.fn = closure ?? throw new LanguageError("Unknown function during function call!");
                            st.code = closure.instructions;
                            st.env = closure.env;
                            st.pc = 0;
                            st.nargs = instr.first.AsInt;
                        }
                        break;

                    case Opcode.SAVE: {
                            // save current vm state to a return value
                            st.stack.Push(new Val(new ReturnAddress(st.fn, GetLabelPosition(instr, st), st.env)));
                        }
                        break;

                    case Opcode.RETURN:
                        if (st.stack.Count > 1) {
                            // preserve return value on top of the stack
                            Val retval = st.stack.Pop();
                            ReturnAddress retaddr = st.stack.Pop().AsReturnAddress;
                            st.stack.Push(retval);

                            // restore vm state from the return value
                            st.fn = retaddr.fn;
                            st.code = retaddr.fn.instructions;
                            st.env = retaddr.env;
                            st.pc = retaddr.pc;
                        } else {
                            st.done = true; // this will force the virtual machine to finish up
                        }
                        break;

                    case Opcode.FN: {
                            var code = instr.first.AsClosure.instructions;
                            st.stack.Push(new Val(new Closure(code, st.env, null)));
                        }
                        break;

                    case Opcode.PRIM: {
                            string name = instr.first.AsString;
                            int argn = (instr.second.IsInt) ? instr.second.AsInt : st.nargs;

                            Primitive prim = Primitives.FindNary(name, argn);
                            if (prim == null) { throw new LanguageError($"Invalid argument count to primitive {name}, count of {argn}"); }

                            Val result = prim.Call(_ctx, argn, st);
                            st.stack.Push(result);
                        }
                        break;

                    default:
                        throw new LanguageError("Unknown instruction type: " + instr.type);
                }
            }

            // return whatever's on the top of the stack
            if (st.stack.Count == 0) {
                throw new LanguageError("Stack underflow!");
            }

            return st.stack.Peek();
        }

        /// <summary> Very naive helper function, finds the position of a given label in the instruction set </summary>
        private int GetLabelPosition (Instruction inst, State st) {
            if (inst.second.IsInt) {
                return inst.second.AsInt;
            } else {
                throw new LanguageError("Unknown jump label: " + inst.first);
            }
        }
    }

}