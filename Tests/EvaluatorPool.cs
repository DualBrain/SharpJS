﻿#pragma warning disable 0420

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace JSIL.Tests {
    public class EvaluatorPool : IDisposable {
        public const int Capacity = 2;

        public readonly string JSShellPath;
        public readonly string Options;
        public readonly Dictionary<string, string> EnvironmentVariables;
        public readonly Action<Evaluator> Initializer;

        private readonly ConcurrentBag<Evaluator> Evaluators = new ConcurrentBag<Evaluator>();
        private readonly AutoResetEvent EvaluatorReadySignal = new AutoResetEvent(false);
        private readonly AutoResetEvent WakeSignal = new AutoResetEvent(false);
        private readonly Thread PoolManager;

        private volatile int IsDisposed = 0;

        public EvaluatorPool (string jsShellPath, string options, Action<Evaluator> initializer, Dictionary<string, string> environmentVariables = null) {
            JSShellPath = jsShellPath;
            Options = options;
            Initializer = initializer;
            EnvironmentVariables = environmentVariables;

            PoolManager = new Thread(ThreadProc);
            PoolManager.Priority = ThreadPriority.AboveNormal;
            PoolManager.IsBackground = true;
            PoolManager.Name = "Evaluator Pool Manager";
            PoolManager.Start();
        }

        ~EvaluatorPool () {
            Dispose();
        }

        public void Dispose () {
            if (Interlocked.CompareExchange(ref IsDisposed, 1, 0) != 0)
                return;

            GC.SuppressFinalize(this);

            // The pool manager might dispose the signal before we get to it.
            try {
                WakeSignal.Set();
            } catch {
            }

            if (!PoolManager.Join(100))
                throw new Exception("Pool manager thread hung");
        }

        public Evaluator Get () {
            Evaluator result;

            var started = DateTime.UtcNow.Ticks;

            while (!Evaluators.TryTake(out result)) {
                WakeSignal.Set();
                EvaluatorReadySignal.WaitOne();
            }

            WakeSignal.Set();

            var ended = DateTime.UtcNow.Ticks;
            // Console.WriteLine("Took {0:0000}ms to get an evaluator", TimeSpan.FromTicks(ended - started).TotalMilliseconds);

            return result;
        }

        private Evaluator CreateEvaluator () {
            var result = new Evaluator(
                JSShellPath, Options, EnvironmentVariables
            );

            Initializer(result);

            return result;
        }

        private void ThreadProc () {
            try {
                while (IsDisposed == 0) {
                    while (Evaluators.Count < Capacity)
                        Evaluators.Add(CreateEvaluator());

                    EvaluatorReadySignal.Set();
                    WakeSignal.WaitOne();
                }
            } finally {
                EvaluatorReadySignal.Dispose();
                WakeSignal.Dispose();

                Evaluator evaluator;
                while (Evaluators.TryTake(out evaluator)) {
                    evaluator.Dispose();
                }
            }
        }
    }

    public class Evaluator : IDisposable {
        public const bool TraceInput = false;
        public const bool TraceOutput = false;

        public readonly Process Process;
        public readonly int Id;

        private static int NextId = 0;

        private volatile int InputClosed = 0;
        private volatile int IsDisposed = 0;
        private volatile int _ExitCode = 0;
        private volatile string _StdOut = null, _StdErr = null;
        private ManualResetEventSlim _DisposedSignal = new ManualResetEventSlim(false);
        private Action _JoinImpl;

        public Evaluator (string jsShellPath, string options, Dictionary<string, string> environmentVariables = null) {
            Id = Interlocked.Increment(ref NextId);

            var psi = new ProcessStartInfo(
                jsShellPath, options
            ) {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (environmentVariables != null) {
                foreach (var kvp in environmentVariables)
                    psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            Process = Process.Start(psi);

            var streamsSignal = new ManualResetEventSlim(false);
            var task = ReadStreams(streamsSignal);

            _JoinImpl = () => {
                WaitHandle.WaitAny(
                    new WaitHandle[] { streamsSignal.WaitHandle, _DisposedSignal.WaitHandle },
                    60000
                );
            };
        }

        private async System.Threading.Tasks.Task ReadStreams (ManualResetEventSlim signal) {
            var stdout = Process.StandardOutput.ReadToEndAsync();
            var stderr = Process.StandardError.ReadToEndAsync();

            _StdOut = FixEncodingIfNeed(await stdout);
            _StdErr = FixEncodingIfNeed(await stderr);

            signal.Set();
        }

        private static string FixEncodingIfNeed(string input)
        {
            if (input!= null && input.StartsWith("獪"))
            {
                return Encoding.UTF8.GetString(Encoding.Unicode.GetBytes(input));
            }

            return input;
        }

        /// <summary>
        /// Not available until process has exited.
        /// </summary>
        public string StandardOutput {
            get {
                return _StdOut;
            }
        }

        /// <summary>
        /// Not available until process has exited.
        /// </summary>
        public string StandardError {
            get {
                return _StdErr;
            }
        }

        public int ExitCode {
            get {
                return _ExitCode;
            }
        }

        public void WriteInput (string text) {
            if (IsDisposed != 0)
                throw new ObjectDisposedException("evaluator");

            if (InputClosed != 0)
                throw new InvalidOperationException("Input stream already closed");

#pragma warning disable 0162
            if (TraceInput) {
                foreach (var line in text.Split(new [] { Environment.NewLine }, StringSplitOptions.None))
                    Debug.WriteLine("{0:X2} in : {1}", Id, line);
            }
#pragma warning restore 0162

            // HACK: Workaround for NUnit/StreamWriter bug on Mono
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            Process.StandardInput.BaseStream.Flush();
            //Process.StandardInput.Write(text);
            //Process.StandardInput.Flush();
        }

        public void WriteInput (string format, params object[] args) {
            WriteInput(String.Format(format, args));
        }

        public void CloseInput () {
            if (IsDisposed != 0)
                throw new ObjectDisposedException("evaluator");

            if (Interlocked.CompareExchange(ref InputClosed, 1, 0) != 0)
				return;

            // HACK: Workaround for NUnit/StreamWriter bug on Mono
            Process.StandardInput.BaseStream.Flush();			
            Process.StandardInput.BaseStream.Close();
            //Process.StandardInput.Flush();
            //Process.StandardInput.Close();
        }

        public void Join () {
            if (IsDisposed != 0)
                throw new ObjectDisposedException("evaluator");

            CloseInput();

            _JoinImpl();
            Process.WaitForExit();
            _ExitCode = Process.ExitCode;

#pragma warning disable 0162
            if (TraceOutput) {
                Debug.WriteLine("{0:X2} exit {1}", Id, _ExitCode);

                foreach (var line in _StdOut.Split(new[] { Environment.NewLine }, StringSplitOptions.None))
                    Debug.WriteLine("{0:X2} out: {1}", Id, line);

                foreach (var line in _StdErr.Split(new[] { Environment.NewLine }, StringSplitOptions.None))
                    Debug.WriteLine("{0:X2} err: {1}", Id, line);
            }
#pragma warning restore 0162
        }

        public void Dispose () {
            if (Interlocked.CompareExchange(ref IsDisposed, 1, 0) != 0)
                return;

            _DisposedSignal.Set();

            // The Process class likes to throw exceptions randomly in accessors and method calls.
            try {
                if (!Process.HasExited) {
                    Process.WaitForExit(1);
                    Process.Kill();
                } else
                    _ExitCode = Process.ExitCode;
            } catch {
            }

            try {
                Process.Close();
            } catch {
            }

            try {
                Process.Dispose();
            } catch {
            }
        }
    }
}
