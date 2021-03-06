using CSLisp.Data;
using CSLisp.Error;
using System.Collections.Generic;
using System.Linq;

namespace CSLisp.Core
{
    /// <summary>
    /// Compiles source s-expression into bytecode.
    /// </summary>
    public class Compiler
    {
        /// <summary> Label counter for each separate compilation block </summary>
        private int _labelNum = 0;

        /// <summary> Internal execution context </summary>
        private readonly Context _ctx = null;

        // some helpful symbol constants, interned only once at the beginning
        private readonly Symbol _quote;
        private readonly Symbol _begin;
        private readonly Symbol _set;
        private readonly Symbol _if;
        private readonly Symbol _ifStar;
        private readonly Symbol _lambda;
        private readonly Symbol _defmacro;

        public Compiler (Context ctx) {
            var global = ctx.packages.global;
            _quote = global.Intern("quote");
            _begin = global.Intern("begin");
            _set = global.Intern("set!");
            _if = global.Intern("if");
            _ifStar = global.Intern("if*");
            _lambda = global.Intern("lambda");
            _defmacro = global.Intern("defmacro");

            _ctx = ctx;

            // initializePrimitives();
        }

        /// <summary> Top level compilation entry point. Compiles the expression x given an empty environment. </summary>
        public Closure Compile (Val x) {
            _labelNum = 0;
            return CompileLambda(Val.NIL, new Cons(x, Val.NIL), null);
        }

        /// <summary> 
        /// Compiles the expression x, given the environment env, into a vector of instructions.
        /// 
        /// Val and More flags are used for tail-call optimization. "Val" is true when 
        /// the expression returns a value that's then used elsewhere. "More" is false when 
        /// the expression represents the final value, true if there is more to compute
        /// (this determines whether we need to jump and return, or just jump)
        /// 
        /// <p> Examples, when compiling expression X:
        /// <ul>
        /// <li> val = t, more = t ... (if X y z) or (f X y)      </li>
        /// <li> val = t, more = f ... (if p X z) or (begin y X)  </li>
        /// <li> val = f, more = t ... (begin X y)                </li>
        /// <li> val = f, more = f ... impossible                 </li>
        /// </ul>
        /// </summary>
        private List<Instruction> Compile (Val x, Environment env, bool val, bool more) {

            // check if macro
            if (IsMacroApplication(x)) {
                return Compile(MacroExpandFull(x), env, val, more);
            }

            if (x.IsSymbol) {       // check if symbol
                return CompileVariable(x.AsSymbol, env, val, more);
            }

            if (x.IsAtom) {         // check if it's not a list
                return CompileConstant(x, val, more);
            }

            // it's not an atom, it's a list, deal with it.
            VerifyExpression(Cons.IsList(x), "Non-list expression detected!");
            Cons cons = x.AsConsOrNull;
            Symbol name = cons.first.AsSymbolOrNull;

            if (name == _quote) {    // (quote value)
                VerifyArgCount(cons, 1);
                return CompileConstant(cons.second, val, more); // second element is the constant
            }
            if (name == _begin) {    // (begin ...)
                return CompileBegin(cons.rest, env, val, more);
            }
            if (name == _set) {      // (set! symbol-name value)
                VerifyArgCount(cons, 2);
                VerifyExpression(cons.second.IsSymbol, "Invalid lvalue in set!, must be a symbol, got: ", cons.second);
                return CompileVarSet(cons.second.AsSymbol, cons.third, env, val, more);
            }
            if (name == _if) {       // (if pred then else) or (if pred then)
                VerifyArgCount(cons, 2, 3);
                return CompileIf(
                    cons.second,     // pred
                    cons.third,      // then
                    (cons.afterThird.IsNotNil ? cons.fourth : Val.NIL), // else
                    env, val, more);
            }
            if (name == _ifStar) {   // (if *pred else)
                VerifyArgCount(cons, 2);
                return CompileIfStar(
                    cons.second,    // pred
                    cons.third,     // else
                    env, val, more);
            }
            if (name == _lambda) {   // (lambda (args...) body...)
                if (!val) {
                    return null;    // it's not used, don't compile
                } else {
                    Cons body = cons.afterSecond.AsConsOrNull;
                    Closure f = CompileLambda(cons.second, body, env);
                    return Merge(
                        Emit(Opcode.MAKE_CLOSURE, new Val(f), Val.NIL, Val.DebugPrint(cons.afterSecond)),
                        IfNot(more, Emit(Opcode.RETURN_VAL)));
                }
            }
            if (name == _defmacro) {
                return CompileAndInstallMacroDefinition(cons.rest.AsConsOrNull, env, val, more);
            }

            return CompileFunctionCall(cons.first, cons.rest.AsConsOrNull, env, val, more);
        }

        /// <summary> 
        /// Verifies arg count of the expression (list of operands). 
        /// Min and max are inclusive; default value of max (= -1) is a special value,
        /// causes max to be treated as equal to min (ie., tests for arg count == min)
        /// </summary>
        private void VerifyArgCount (Cons cons, int min, int max = -1) {
            max = (max >= 0) ? max : min;  // default value means: max == min
            int count = Cons.Length(cons.rest);
            if (count < min || count > max) {
                throw new CompilerError("Invalid argument count in expression " + cons +
                    ": " + count + " supplied, expected in range [" + min + ", " + max + "]");
            }
        }

        /// <summary> Verifies that the expression is true, throws the specified error otherwise. </summary>
        private void VerifyExpression (bool condition, string message, Val? val = null) {
            if (!condition) {
                throw new CompilerError(message + (val.HasValue ? (" " + val.Value.type) : ""));
            }
        }

        /// <summary> Returns true if the given value is a macro </summary>
        private bool IsMacroApplication (Val x) {
            var cons = x.AsConsOrNull;
            return
                cons != null &&
                cons.first.IsSymbol &&
                cons.first.AsSymbol.pkg.HasMacro(cons.first.AsSymbol);
        }

        /// <summary> Performs compile-time macroexpansion, one-level deep </summary>
        public Val MacroExpand1Step (Val exp) {
            Cons cons = exp.AsConsOrNull;
            if (cons == null || !cons.first.IsSymbol) { return exp; } // something unexpected

            Symbol name = cons.first.AsSymbol;
            Macro macro = name.pkg.GetMacro(name);
            if (macro == null) { return exp; } // no such macro, ignore

            // now we execute the macro at compile time, in the same context...
            Val result = _ctx.vm.Execute(macro.body, Cons.ToNativeList(cons.rest).ToArray());
            return result;
        }

        /// <summary> Performs compile-time macroexpansion, fully recursive </summary>
        public Val MacroExpandFull (Val exp) {
            Val expanded = MacroExpand1Step(exp);
            Cons cons = expanded.AsConsOrNull;
            if (cons == null || !cons.first.IsSymbol) { return expanded; } // nothing more to expand

            // if we're expanding a list, replace each element recursively
            while (cons != null) {
                Cons elt = cons.first.AsConsOrNull;
                if (elt != null && elt.first.IsSymbol) {
                    Val substitute = MacroExpandFull(cons.first);
                    cons.first = substitute;
                }
                cons = cons.rest.AsConsOrNull;
            }

            return expanded;
        }

        /// <summary> Compiles a variable lookup </summary>
        private List<Instruction> CompileVariable (Symbol x, Environment env, bool val, bool more) {
            if (!val) { return null; }

            var pos = Environment.GetVariable(x, env);
            bool isLocal = pos.IsValid;
            return Merge(
                (isLocal ?
                    Emit(Opcode.LOCAL_GET, pos.frameIndex, pos.symbolIndex, Val.DebugPrint(x)) :
                    Emit(Opcode.GLOBAL_GET, x)),
                IfNot(more, Emit(Opcode.RETURN_VAL)));
        }

        /// <summary> Compiles a constant, if it's actually used elsewhere </summary>
        private List<Instruction> CompileConstant (Val x, bool val, bool more) {
            if (!val) { return null; }

            return Merge(
                Emit(Opcode.PUSH_CONST, x, Val.NIL),
                IfNot(more, Emit(Opcode.RETURN_VAL)));
        }

        /// <summary> Compiles a sequence defined by a BEGIN - we pop all values, except for the last one </summary>
        private List<Instruction> CompileBegin (Val exps, Environment env, bool val, bool more) {
            if (exps.IsNil) {
                return CompileConstant(Val.NIL, val, more); // (begin)
            }

            Cons cons = exps.AsConsOrNull;
            VerifyExpression(cons != null, "Unexpected value passed to begin block, instead of a cons:", exps);

            if (cons.rest.IsNil) {  // length == 1
                return Compile(cons.first, env, val, more);
            } else {
                return Merge(
                    Compile(cons.first, env, false, true),  // note: not the final expression, set val = f, more = t
                    CompileBegin(cons.rest, env, val, more));
            }
        }

        /// <summary> Compiles a variable set </summary>
        private List<Instruction> CompileVarSet (Symbol x, Val value, Environment env, bool val, bool more) {
            var pos = Environment.GetVariable(x, env);
            bool isLocal = pos.IsValid;
            return Merge(
                Compile(value, env, true, true),
                (isLocal ?
                        Emit(Opcode.LOCAL_SET, pos.frameIndex, pos.symbolIndex, Val.DebugPrint(x)) :
                        Emit(Opcode.GLOBAL_SET, x)),
                IfNot(val, Emit(Opcode.STACK_POP)),
                IfNot(more, Emit(Opcode.RETURN_VAL))
                );
        }

        /// <summary> Compiles an if statement (fun!) </summary>
        private List<Instruction> CompileIf (Val pred, Val then, Val els, Environment env, bool val, bool more) {
            // (if #f x y) => y
            if (pred.IsBool && !pred.AsBool) { return Compile(els, env, val, more); }

            // (if #t x y) => x, or (if 5 ...) or (if "foo" ...)
            bool isConst = (pred.IsBool) || (pred.IsNumber) || (pred.IsString);
            if (isConst) { return Compile(then, env, val, more); }

            // (if (not p) x y) => (if p y x)
            if (Cons.IsList(pred)) {
                var cons = pred.AsConsOrNull;
                bool isNotTest =
                    Cons.Length(cons) == 2 &&
                    cons.first.IsSymbol &&
                    cons.first.AsSymbol.fullName == "not";  // TODO: this should make sure it's a const not just a symbol

                if (isNotTest) { return CompileIf(cons.second, els, then, env, val, more); }
            }

            // it's more complicated...
            List<Instruction> PredCode = Compile(pred, env, true, true);
            List<Instruction> ThenCode = Compile(then, env, val, more);
            List<Instruction> ElseCode = els.IsNotNil ? Compile(els, env, val, more) : null;

            // (if p x x) => (begin p x)
            if (CodeEquals(ThenCode, ElseCode)) {
                return Merge(
                    Compile(pred, env, false, true),
                    ElseCode);
            }

            // (if p nil y) => p (TJUMP L2) y L2:
            if (ThenCode == null) {
                string l2 = MakeLabel();
                return Merge(
                    PredCode,
                    Emit(Opcode.JMP_IF_TRUE, l2),
                    ElseCode,
                    Emit(Opcode.MAKE_LABEL, l2),
                    IfNot(more, Emit(Opcode.RETURN_VAL)));
            }

            // (if p x) => p (FJUMP L1) x L1:
            if (ElseCode == null) {
                string l1 = MakeLabel();
                return Merge(
                    PredCode,
                    Emit(Opcode.JMP_IF_FALSE, l1),
                    ThenCode,
                    Emit(Opcode.MAKE_LABEL, l1),
                    IfNot(more, Emit(Opcode.RETURN_VAL)));
            }

            // (if p x y) => p (FJUMP L1) x L1: y 
            //         or    p (FJUMP L1) x (JUMP L2) L1: y L2:
            // depending on whether this is the last exp, or if there's more
            if (more) {
                string l1 = MakeLabel();
                string l2 = MakeLabel();
                return Merge(
                    PredCode,
                    Emit(Opcode.JMP_IF_FALSE, l1),
                    ThenCode,
                    Emit(Opcode.JMP_TO_LABEL, l2),
                    Emit(Opcode.MAKE_LABEL, l1),
                    ElseCode,
                    Emit(Opcode.MAKE_LABEL, l2));
            } else {
                string l1 = MakeLabel();
                return Merge(
                    PredCode,
                    Emit(Opcode.JMP_IF_FALSE, l1),
                    ThenCode,
                    Emit(Opcode.MAKE_LABEL, l1),
                    ElseCode);
            }
        }

        /// <summary> Compiles an if* statement </summary>
        private List<Instruction> CompileIfStar (Val pred, Val els, Environment env, bool val, bool more) {

            // (if* x y) will return x if it's not false, otherwise it will return y

            // (if* #f x) => x
            if (pred.IsBool && !pred.AsBool) {
                return Compile(els, env, val, more);
            }

            List<Instruction> PredCode = Compile(pred, env, true, true);
            List<Instruction> ElseCode = els.IsNotNil ? Compile(els, env, true, more) : null;

            // (if* p x) => p (DUPE) (TJUMP L1) (POP) x L1: (POP?)
            string l1 = MakeLabel();
            return Merge(
                PredCode,
                Emit(Opcode.DUPLICATE),
                Emit(Opcode.JMP_IF_TRUE, l1),
                Emit(Opcode.STACK_POP),
                ElseCode,
                IfNot(more || val, Emit(Opcode.RETURN_VAL)),
                Emit(Opcode.MAKE_LABEL, l1),
                IfNot(val, Emit(Opcode.STACK_POP)),
                IfNot(more, Emit(Opcode.RETURN_VAL)));
        }

        /// <summary> Compiles code to produce a new closure </summary>
        private Closure CompileLambda (Val args, Cons body, Environment env) {
            Environment newEnv = Environment.Make(MakeTrueList(args), env);
            List<Instruction> instructions = Merge(
                EmitArgs(args, 0),
                CompileBegin(new Val(body), newEnv, true, false));

            Code.Handle handle = _ctx.code.Register(Assemble(instructions), "");
            return new Closure(handle, env, args.AsConsOrNull, "");
        }

        /// <summary> Compile a list, leaving all elements on the stack </summary>
        private List<Instruction> CompileList (Cons exps, Environment env) =>
            (exps == null)
                ? null
                : Merge(
                    Compile(exps.first, env, true, true),
                    CompileList(exps.rest.AsConsOrNull, env));

        /// <summary> 
        /// Compiles a macro, and sets the given symbol to point to it. NOTE: unlike all other expressions,
        /// which are executed by the virtual machine, this happens immediately, during compilation.
        /// </summary>
        private List<Instruction> CompileAndInstallMacroDefinition (Cons cons, Environment env, bool val, bool more) {

            // example: (defmacro foo (x) (+ x 1))
            Symbol name = cons.first.AsSymbol;
            Cons args = cons.second.AsCons;
            Cons bodylist = cons.afterSecond.AsConsOrNull;
            Closure body = CompileLambda(new Val(args), bodylist, env);
            Macro macro = new Macro(name, args, body);

            // install it in the package
            name.pkg.SetMacro(name, macro);
            return CompileConstant(Val.NIL, val, more);
        }

        /// <summary> Compile the application of a function to arguments </summary>
        private List<Instruction> CompileFunctionCall (Val f, Cons args, Environment env, bool val, bool more) {
            if (f.IsCons) {
                var fcons = f.AsCons;
                if (fcons.first.IsSymbol && fcons.first.AsSymbol.fullName == "lambda" && fcons.second.IsNil) {
                    // ((lambda () body)) => (begin body)
                    VerifyExpression(args == null, "Too many arguments supplied!");
                    return CompileBegin(fcons.afterSecond, env, val, more);
                }
            }

            if (more) {
                // need to save the continuation point
                string k = MakeLabel("K");
                return Merge(
                    Emit(Opcode.SAVE_RETURN, k),
                    CompileList(args, env),
                    Compile(f, env, true, true),
                    Emit(Opcode.JMP_CLOSURE, Cons.Length(args)),
                    Emit(Opcode.MAKE_LABEL, k),
                    IfNot(val, Emit(Opcode.STACK_POP)));
            } else {
                // function call as rename plus goto
                return Merge(
                    CompileList(args, env),
                    Compile(f, env, true, true),
                    Emit(Opcode.JMP_CLOSURE, Cons.Length(args)));
            }
        }

        /// <summary> Generates an appropriate ARGS or ARGSDOT sequence, making a new stack frame </summary>
        private List<Instruction> EmitArgs (Val args, int nSoFar) {
            // recursively detect whether it's a list or ends with a dotted cons, and generate appropriate arg

            // terminal case
            if (args.IsNil) { return Emit(Opcode.MAKE_ENV, nSoFar); }        // (lambda (a b c) ...)
            if (args.IsSymbol) { return Emit(Opcode.MAKE_ENVDOT, nSoFar); }  // (lambda (a b . c) ...)

            // if not at the end, recurse
            var cons = args.AsConsOrNull;
            if (cons != null && cons.first.IsSymbol) { return EmitArgs(cons.rest, nSoFar + 1); }

            throw new CompilerError("Invalid argument list");           // (lambda (a b 5 #t) ...) or some other nonsense
        }

        /// <summary> Converts a dotted cons list into a proper non-dotted one </summary>
        private Cons MakeTrueList (Val dottedList) {

            // we reached a terminating nil - return as is
            if (dottedList.IsNil) { return null; }

            // we reached a terminating cdr in a dotted pair - convert it
            if (dottedList.IsAtom) { return new Cons(dottedList, Val.NIL); }

            var cons = dottedList.AsCons;
            return new Cons(cons.first, MakeTrueList(cons.rest)); // keep recursing
        }

        /// <summary> Generates a sequence containing a single instruction </summary>
        private List<Instruction> Emit (Opcode type, Val first, Val second, string debug = null) =>
            new List<Instruction>() { new Instruction(type, first, second, debug) };

        /// <summary> Generates a sequence containing a single instruction </summary>
        private List<Instruction> Emit (Opcode type, Val first) =>
            new List<Instruction>() { new Instruction(type, first) };

        /// <summary> Generates a sequence containing a single instruction with no arguments </summary>
        private List<Instruction> Emit (Opcode type) =>
            new List<Instruction>() { new Instruction(type) };


        /// <summary> Creates a new unique label </summary>
        private string MakeLabel (string prefix = "L") =>
            prefix + _labelNum++.ToString();

        /// <summary> Merges sequences of instructions into a single sequence </summary>
        private List<Instruction> Merge (params List<Instruction>[] elements) =>
            elements.Where(list => list != null).SelectMany(instr => instr).ToList();

        /// <summary> Returns the value if the condition is false, null if it's true </summary>
        private List<Instruction> IfNot (bool test, List<Instruction> value) =>
            !test ? value : null;

        /// <summary> Compares two code sequences, and returns true if they're equal </summary>
        private bool CodeEquals (List<Instruction> a, List<Instruction> b) {
            if (a == null && b == null) { return true; }
            if (a == null || b == null || a.Count != b.Count) { return false; }

            for (int i = 0; i < a.Count; i++) {
                if (!Instruction.Equal(a[i], b[i])) {
                    return false;
                }
            }
            return true;
        }

        /// <summary> 
        /// "Assembles" the compiled code, by resolving label references and converting them to index offsets. 
        /// Modifies the code data structure in place, and returns it back to the caller.
        /// </summary>
        private List<Instruction> Assemble (List<Instruction> code) {
            var positions = new LabelPositions(code);

            for (int i = 0; i < code.Count; i++) {
                Instruction inst = code[i];

                if (inst.IsJump) {
                    int pos = positions.FindPosition(inst.first);
                    if (pos >= 0) {
                        inst.UpdateJumpDestination(pos);
                    } else {
                        throw new CompilerError($"Can't find jump label {inst.first} during assembly");
                    }
                }
            }

            return code;
        }

        /// <summary>
        /// Temporary data structure used during assembly: holds code positions for all labels
        /// </summary>
        private class LabelPositions : Dictionary<string, int>
        {
            public LabelPositions (List<Instruction> code) {
                for (int i = 0; i < code.Count; i++) {
                    Instruction inst = code[i];
                    if (inst.type == Opcode.MAKE_LABEL) {
                        string label = inst.first.AsString;
                        this[label] = i;
                    }
                }
            }

            /// <summary> Returns code position of the given label, or -1 if not found or the value is not a label. </summary>
            public int FindPosition (Val label) {
                if (!label.IsString) { return -1; }
                return TryGetValue(label.AsString, out int pos) ? pos : -1;
            }
        }

    }

}