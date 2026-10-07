#include <CLibUtilsQTR/PresetHelpers/YAMLTemplates.hpp>
#include <yaml-cpp/emitfromevents.h>
#include <algorithm>
#include <array>
#include <cstdlib>
#include <iostream>
#include <stdexcept>
#include <string_view>
#include <vector>

enum class ExpansionContext { Document, Rules, Rule, Other };

std::size_t RuleFieldPriority(const YAML::Node& key) {
    constexpr std::array<std::string_view, 6> fields{
        "owners", "forms", "stages", "finalFormEditorID", "transformers", "timeModulators"};
    if (key.IsScalar()) {
        for (std::size_t index = 0; index < fields.size(); ++index) {
            if (key.Scalar() == fields[index]) return index;
        }
    }
    return fields.size();
}

std::vector<YAML::const_iterator> OrderedMapEntries(const YAML::Node& node, bool rule) {
    std::vector<YAML::const_iterator> entries;
    for (auto entry = node.begin(); entry != node.end(); ++entry) entries.push_back(entry);
    if (rule) {
        std::stable_sort(entries.begin(), entries.end(), [](const auto& left, const auto& right) {
            return RuleFieldPriority(left->first) < RuleFieldPriority(right->first);
        });
    }
    return entries;
}

void EmitExpanded(YAML::Emitter& output, YAML::EmitFromEvents& events, const YAML::Node& node,
                  std::vector<YAML::Node>& ancestors, ExpansionContext context) {
    if (node.IsScalar()) {
        constexpr auto stringTag = "tag:yaml.org,2002:str";
        const auto& tag = node.Tag();
        // Ordinary quotes do not suppress AoT template calls.
        if ((tag == "!" || tag == stringTag) && node.Scalar().find('(') == std::string::npos) {
            output << YAML::DoubleQuoted;
            events.OnScalar(node.Mark(), {}, YAML::NullAnchor, node.Scalar());
        } else {
            events.OnScalar(node.Mark(), tag == "!" ? stringTag : tag, YAML::NullAnchor, node.Scalar());
        }
    } else if (node.IsNull()) {
        events.OnNull(node.Mark(), YAML::NullAnchor);
    } else {
        if (std::any_of(ancestors.begin(), ancestors.end(), [&](const auto& ancestor) { return ancestor.is(node); })) {
            throw YAML::RepresentationException(node.Mark(), "Circular YAML alias");
        }
        ancestors.push_back(node);
        if (node.IsSequence()) {
            events.OnSequenceStart(node.Mark(), node.Tag(), YAML::NullAnchor, YAML::EmitterStyle::Block);
            const auto childContext = context == ExpansionContext::Rules ? ExpansionContext::Rule : ExpansionContext::Other;
            for (const auto& child : node) EmitExpanded(output, events, child, ancestors, childContext);
            events.OnSequenceEnd();
        } else if (node.IsMap()) {
            events.OnMapStart(node.Mark(), node.Tag(), YAML::NullAnchor, YAML::EmitterStyle::Block);
            for (const auto& iterator : OrderedMapEntries(node, context == ExpansionContext::Document || context == ExpansionContext::Rule)) {
                const auto& entry = *iterator;
                auto childContext = ExpansionContext::Other;
                if (context == ExpansionContext::Document && entry.first.IsScalar() &&
                    (entry.first.Scalar() == "ownerLists" || entry.first.Scalar() == "formsLists")) {
                    childContext = ExpansionContext::Rules;
                }
                EmitExpanded(output, events, entry.first, ancestors, ExpansionContext::Other);
                EmitExpanded(output, events, entry.second, ancestors, childContext);
            }
            events.OnMapEnd();
        }
        ancestors.pop_back();
    }
}

int main() try {
    auto document = YAML::Load(std::cin);
    PresetHelpers::YAML_Helpers::ResolveTemplates(document);
    YAML::Emitter output;
    YAML::EmitFromEvents events(output);
    std::vector<YAML::Node> ancestors;
    EmitExpanded(output, events, document, ancestors, ExpansionContext::Document);
    if (!output.good()) throw std::runtime_error(output.GetLastError());
    std::cout << output.c_str() << '\n';
    return std::cout ? EXIT_SUCCESS : EXIT_FAILURE;
} catch (const std::exception& error) {
    std::cerr << error.what() << '\n';
    return EXIT_FAILURE;
}
