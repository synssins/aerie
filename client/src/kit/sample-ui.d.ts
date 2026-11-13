// The sample plugin's script (plain JavaScript, outside this folder), as the test that runs it sees it.
declare module "kb-sample-ui" {
  const start: (kb: import("./host").KbHost) => void;
  export default start;
}
