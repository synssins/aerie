import { useEffect, useState } from "react";
import { api } from "./api";

/** Sent when an Admin saves Administration › AI, so the feature buttons follow without a reload. */
export const AI_CHANGED = "kb:ai-changed";
export const aiChanged = () => window.dispatchEvent(new Event(AI_CHANGED));

/** Which AI features are open to the signed-in person: read at sign-in and again whenever the AI settings change. */
export function useAiFeatures(signedIn: boolean) {
  const [ai, setAi] = useState({ canImport: false, canFormat: false, canAssist: false });
  useEffect(() => {
    if (!signedIn) return;
    const load = () => void api.get<{ canImport: boolean; canFormat: boolean; canAssist?: boolean }>("/imports")
      .then((l) => setAi({ canImport: l.canImport, canFormat: l.canFormat, canAssist: !!l.canAssist })).catch(() => undefined);
    load();
    window.addEventListener(AI_CHANGED, load);
    return () => window.removeEventListener(AI_CHANGED, load);
  }, [signedIn]);
  return ai;
}
