package com.rextechnologies.sightline.protocol.gpsock

import org.w3c.dom.Document
import org.w3c.dom.Element
import org.w3c.dom.NodeList
import org.xml.sax.ErrorHandler
import org.xml.sax.InputSource
import org.xml.sax.SAXException
import org.xml.sax.SAXParseException
import java.io.StringReader
import javax.xml.parsers.DocumentBuilderFactory

/**
 * What kind of setting a menu entry is, from its `Type` field.
 *
 * @property code The number in the `Type` field.
 */
enum class MenuSettingKind(val code: Int) {
    /** A choice from a fixed list. Nearly everything is one of these. */
    Choice(0x00),

    /** An action rather than a value: formatting the card, restoring defaults. */
    Action(0x01),

    /** Free text, such as the Wi-Fi name and password. */
    Text(0x02),

    /** Something the camera reports and will not let you change, such as its version. */
    ReadOnly(0x03),
    ;

    companion object {
        /** The kind numbered [code], or null when this library does not know it. */
        fun fromCode(code: Int): MenuSettingKind? = entries.firstOrNull { it.code == code }
    }
}

/**
 * One value a [MenuSetting] will accept.
 *
 * @property value The number written to the camera.
 * @property label What the camera calls it, in the language the menu was fetched in.
 */
data class MenuChoice(val value: Int, val label: String)

/**
 * One setting the camera supports.
 *
 * @property id The menu id, as [GpSockCommand.MenuSetParameter] takes it.
 * @property name The camera's own name for it.
 * @property category The group the camera files it under.
 * @property kind Whether it is a choice, an action, text, or read-only; null for a kind this library
 *   does not know, which is never treated as writable.
 * @property default The camera's default value.
 * @property choices The values it will accept, empty for a non-choice setting.
 */
data class MenuSetting(
    val id: Int,
    val name: String,
    val category: String,
    val kind: MenuSettingKind?,
    val default: Int,
    val choices: List<MenuChoice>,
) {
    /** Whether this setting can be written at all. */
    val isWritable: Boolean
        get() = kind == MenuSettingKind.Choice || kind == MenuSettingKind.Text

    /** What the camera calls [value], or the number when it has no name. */
    fun labelFor(value: Int): String {
        for (choice in choices) {
            if (choice.value == value) {
                return choice.label
            }
        }

        return value.toString()
    }
}

/**
 * Every setting a camera supports, read from the camera rather than hardcoded.
 *
 * The camera answers [GpSockCommand.GetParameterFile] with its own menu as XML. Taking the catalogue
 * from there rather than from a table in this app is what lets a camera with a different firmware, a
 * different language, or a feature this one does not have still show the right settings with the
 * right options — and what stops the app offering a resolution the camera would refuse.
 *
 * @property settings Every setting, in the order the camera listed them.
 */
class MenuCatalog private constructor(val settings: List<MenuSetting>) {
    /** The category names, in the camera's order. */
    val categories: List<String>
        get() = settings.map { it.category }.distinct()

    /** The setting with this id, or null when the camera has no such setting. */
    fun find(id: Int): MenuSetting? = settings.firstOrNull { it.id == id }

    companion object {
        private const val CLOSING_TAG = "</Menu>"
        private const val NOT_WELL_FORMED = "The camera's menu is not well-formed XML."

        /**
         * Reads a catalogue from the camera's menu XML.
         *
         * @param xml The bytes the camera sent, which may carry padding after the final tag.
         * @throws GpSockProtocolException The bytes are not a menu this app can read.
         */
        fun parse(xml: ByteArray): MenuCatalog = parse(xml.toString(Charsets.UTF_8))

        /**
         * Reads a catalogue from the camera's menu XML.
         *
         * @param xml The document, which may carry padding after the final tag.
         * @throws GpSockProtocolException The text is not a menu this app can read.
         */
        fun parse(xml: String): MenuCatalog {
            // The last chunk is padded to the camera's buffer size, so there is usually rubbish after
            // the closing tag. Trimming to it is what makes the document well-formed.
            val end = xml.lastIndexOf(CLOSING_TAG)
            if (end < 0) {
                throw GpSockProtocolException("The menu XML has no closing </Menu> tag.")
            }

            val document = read(xml.substring(0, end + CLOSING_TAG.length))
            val parsed = mutableListOf<MenuSetting>()
            for (category in document.getElementsByTagName("Category").elements()) {
                val categoryName = text(category.child("Name")) ?: "Other"
                for (setting in category.getElementsByTagName("Setting").elements()) {
                    val id = number(text(setting.child("ID")))
                    val name = text(setting.child("Name"))
                    if (id == null || name == null) {
                        // A setting with no id cannot be written and a setting with no name cannot be
                        // shown, so there is nothing useful to do with it except leave it out.
                        continue
                    }

                    val choices = mutableListOf<MenuChoice>()
                    for (value in setting.getElementsByTagName("Value").elements()) {
                        val choiceId = number(text(value.child("ID")))
                        val label = text(value.child("Name"))
                        if (choiceId != null && label != null) {
                            choices += MenuChoice(choiceId, label)
                        }
                    }

                    parsed += MenuSetting(
                        id,
                        name,
                        categoryName,
                        MenuSettingKind.fromCode(number(text(setting.child("Type"))) ?: 0),
                        number(text(setting.child("Default"))) ?: 0,
                        choices,
                    )
                }
            }

            if (parsed.isEmpty()) {
                throw GpSockProtocolException("The camera's menu listed no settings.")
            }

            return MenuCatalog(parsed)
        }

        private fun read(xml: String): Document {
            // A document type declaration is how XML reaches for other files and expands itself
            // without limit. The camera's menu never has one, so one is refused here, before any
            // parser acts on it: Android's parser takes none of the JAXP settings that would make
            // reading one safe, and this has to behave the same on the JVM and on a phone.
            if (xml.contains("<!DOCTYPE")) {
                throw GpSockProtocolException("$NOT_WELL_FORMED It declares a document type, which is never read.")
            }

            val builder = DocumentBuilderFactory.newInstance().newDocumentBuilder()
            builder.setErrorHandler(QuietParserErrors)
            return try {
                builder.parse(InputSource(StringReader(xml)))
            } catch (exception: SAXException) {
                throw GpSockProtocolException(NOT_WELL_FORMED, exception)
            }
        }

        private fun NodeList.elements(): List<Element> = List(length) { item(it) as Element }

        /** The first child element called [name], as LINQ to XML's `Element(name)` finds it: never a grandchild. */
        private fun Element.child(name: String): Element? {
            var node = firstChild
            while (node != null) {
                if (node is Element && node.tagName == name) {
                    return node
                }
                node = node.nextSibling
            }
            return null
        }

        private fun text(element: Element?): String? {
            if (element == null) {
                return null
            }

            val value = element.textContent.trim()
            return value.ifEmpty { null }
        }

        /** Reads the camera's numbers, which it writes as `0x0000100` or plainly. */
        private fun number(text: String?): Int? {
            if (text == null) {
                return null
            }

            if (text.startsWith("0x", ignoreCase = true)) {
                return text.substring(2).toUIntOrNull(16)?.toInt()
            }

            return text.toIntOrNull()
        }
    }
}

/**
 * Stops a parse at its first error rather than also printing it to standard error, which is what the
 * JDK's parser does for a document nobody gave it an error handler for.
 */
internal object QuietParserErrors : ErrorHandler {
    /** A warning is not a failure, and nothing reads it. */
    override fun warning(exception: SAXParseException) = Unit

    override fun error(exception: SAXParseException): Unit = throw exception

    override fun fatalError(exception: SAXParseException): Unit = throw exception
}
