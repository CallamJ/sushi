package dev.sushi.jetbrains

import com.intellij.extapi.psi.ASTWrapperPsiElement
import com.intellij.extapi.psi.PsiFileBase
import com.intellij.lang.ASTNode
import com.intellij.lang.CodeDocumentationAwareCommenter
import com.intellij.lang.ParserDefinition
import com.intellij.lang.PsiBuilder
import com.intellij.lang.PsiParser
import com.intellij.lexer.Lexer
import com.intellij.psi.FileViewProvider
import com.intellij.psi.PsiElement
import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType
import com.intellij.psi.tree.IFileElementType
import com.intellij.psi.tree.TokenSet

/**
 * Sushi's local PSI tree.  The first version deliberately preserves every
 * token under one file root: it is lossless, robust while editing incomplete
 * scripts, and gives IntelliJ a real language PSI instead of treating files
 * as plain text.  Grammar-level nodes can now be introduced incrementally
 * without replacing the file type or editor integration.
 */
class SushiParserDefinition : ParserDefinition {
    override fun createLexer(project: com.intellij.openapi.project.Project?): Lexer = SushiLexer()
    override fun createParser(project: com.intellij.openapi.project.Project?): PsiParser = SushiParser()
    override fun getFileNodeType(): IFileElementType = SushiFileElementType
    override fun getCommentTokens(): TokenSet = TokenSet.create(SushiTokenTypes.COMMENT, SushiTokenTypes.DOC_COMMENT)
    override fun getStringLiteralElements(): TokenSet = TokenSet.create(SushiTokenTypes.STRING)
    override fun createElement(node: ASTNode): PsiElement = ASTWrapperPsiElement(node)
    override fun createFile(viewProvider: FileViewProvider) = SushiPsiFile(viewProvider)
    override fun spaceExistenceTypeBetweenTokens(left: ASTNode, right: ASTNode) = ParserDefinition.SpaceRequirements.MAY
}

object SushiFileElementType : IFileElementType(SushiLanguage)

private class SushiParser : PsiParser {
    override fun parse(root: IElementType, builder: PsiBuilder): ASTNode {
        val file = builder.mark()
        while (!builder.eof()) builder.advanceLexer()
        file.done(root)
        return builder.treeBuilt
    }
}

class SushiPsiFile(viewProvider: FileViewProvider) : PsiFileBase(viewProvider, SushiLanguage) {
    override fun getFileType() = SushiFileType
    override fun toString() = "Sushi File"
}

/** Lets IntelliJ's built-in Enter and comment actions recognize Sushi's /// docs. */
class SushiCommenter : CodeDocumentationAwareCommenter {
    override fun getLineCommentPrefix() = "//"
    override fun getBlockCommentPrefix() = "/*"
    override fun getBlockCommentSuffix() = "*/"
    override fun getCommentedBlockCommentPrefix() = "/*"
    override fun getCommentedBlockCommentSuffix() = "*/"
    override fun getLineCommentTokenType() = SushiTokenTypes.COMMENT
    override fun getBlockCommentTokenType() = SushiTokenTypes.COMMENT
    override fun getDocumentationCommentTokenType() = SushiTokenTypes.DOC_COMMENT
    override fun getDocumentationCommentPrefix() = "///"
    override fun getDocumentationCommentLinePrefix() = "///"
    override fun getDocumentationCommentSuffix() = ""
    override fun getDocumentationLineCommentTokenType() = SushiTokenTypes.DOC_COMMENT
    override fun getDocumentationLineCommentPrefix() = "///"
    override fun isDocumentationComment(element: com.intellij.psi.PsiComment) =
        element.node.elementType == SushiTokenTypes.DOC_COMMENT
    override fun isDocumentationLineComment(element: com.intellij.psi.PsiComment) =
        element.node.elementType == SushiTokenTypes.DOC_COMMENT
}
