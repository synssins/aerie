export const LIBRARIES: string[];
export function fileOf(library: string): string;
export function moduleFor(library: string): Promise<string>;
export function generate(outDir: string): Promise<void>;
