import { createContext, useCallback, useContext, useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";

/** In-app replacements for window.prompt/confirm/alert. Every dialog renders inside the page. */
type AskOptions = { title: string; label: string; initial?: string; ok?: string; validate?: (v: string) => string | null };
type ConfirmOptions = { title: string; body?: string; ok?: string; cancel?: string; danger?: boolean };
type Dialogs = {
  ask: (o: AskOptions) => Promise<string | null>;
  confirm: (o: ConfirmOptions) => Promise<boolean>;
};

type Pending =
  | { kind: "ask"; o: AskOptions; resolve: (v: string | null) => void }
  | { kind: "confirm"; o: ConfirmOptions; resolve: (v: boolean) => void };

const Ctx = createContext<Dialogs | null>(null);

export function useDialogs(): Dialogs {
  const d = useContext(Ctx);
  if (!d) throw new Error("DialogProvider missing");
  return d;
}

export function DialogProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null);
  const ask = useCallback((o: AskOptions) => new Promise<string | null>((resolve) => setPending({ kind: "ask", o, resolve })), []);
  const confirm = useCallback((o: ConfirmOptions) => new Promise<boolean>((resolve) => setPending({ kind: "confirm", o, resolve })), []);
  return (
    <Ctx.Provider value={{ ask, confirm }}>
      {children}
      {pending && <DialogView key={Date.now()} pending={pending} done={() => setPending(null)} />}
    </Ctx.Provider>
  );
}

function DialogView({ pending, done }: { pending: Pending; done: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [value, setValue] = useState(pending.kind === "ask" ? pending.o.initial ?? "" : "");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { ref.current?.showModal(); }, []);

  function cancel() {
    if (pending.kind === "ask") pending.resolve(null); else pending.resolve(false);
    done();
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    if (pending.kind === "ask") {
      const v = value.trim();
      const err = !v ? "Required." : pending.o.validate?.(v) ?? null;
      if (err) { setError(err); return; }
      pending.resolve(v);
    } else pending.resolve(true);
    done();
  }

  const o = pending.o;
  return (
    <dialog ref={ref} className="dlg" onCancel={(e) => { e.preventDefault(); cancel(); }} aria-labelledby="dlg-title">
      <form onSubmit={submit}>
        <h2 id="dlg-title">{o.title}</h2>
        {pending.kind === "ask" ? (
          <label>
            {pending.o.label}
            <input autoFocus value={value} onChange={(e) => { setValue(e.target.value); setError(null); }} maxLength={200} />
          </label>
        ) : (
          pending.o.body && <p className="muted">{pending.o.body}</p>
        )}
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="dlg-actions">
          <button type="button" className="tb" onClick={cancel}>{(pending.kind === "confirm" && pending.o.cancel) || "Cancel"}</button>
          <button type="submit" className={`tb ${pending.kind === "confirm" && pending.o.danger ? "danger" : "primary"}`} autoFocus={pending.kind === "confirm"}>
            {o.ok ?? "OK"}
          </button>
        </div>
      </form>
    </dialog>
  );
}
