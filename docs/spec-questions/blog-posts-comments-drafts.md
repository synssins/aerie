# Blog posts, comments and unpublished drafts

- **Building:** the content operations (`contracts/behaviour/content.md`).
- **Operations:** `createContent`, `listContent`, `scanContent`, `getChildren`, `getChildComments`, `getChildrenOfType`, `getDescendants`, `getDescendantsOfType`, `getSpaceContent`, `getSpaceContentOfType`, and the `status=draft` forms of `getContent`, `updateContent` and `deleteContent`.

## Question

The contract has three kinds of content aerie does not hold: blog posts, comments, and drafts of content that was never published. Aerie's pages have no posting day, aerie has no comments on pages yet, and an aerie draft is a person's unsaved edits to a page that exists, not a new unpublished page. Should aerie gain any of them (each is a new data model), and until then, what should these operations answer?

## What aerie does until answered

- Lists of blog posts and of comments are empty (`size: 0`), in maps and on their own.
- Creating a blog post or a comment gives `400`, as does `status=draft` on create.
- Reading, updating or deleting with `status=draft` finds nothing: `404`.

See `src/Kb.Web/Actions/DataCenterContent.cs`.
