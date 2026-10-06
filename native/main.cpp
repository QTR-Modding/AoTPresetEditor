#include <CLibUtilsQTR/PresetHelpers/YAMLTemplates.hpp>
#include <yaml-cpp/emitfromevents.h>
#include <algorithm>
#include <cstdlib>
#include <iostream>
#include <stdexcept>
#include <vector>

void EmitExpanded(YAML::Emitter& output, YAML::EmitFromEvents& events, const YAML::Node& node, std::vector<YAML::Node>& ancestors) {
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
            for (const auto& child : node) EmitExpanded(output, events, child, ancestors);
            events.OnSequenceEnd();
        } else if (node.IsMap()) {
            events.OnMapStart(node.Mark(), node.Tag(), YAML::NullAnchor, YAML::EmitterStyle::Block);
            for (const auto& entry : node) {
                EmitExpanded(output, events, entry.first, ancestors);
                EmitExpanded(output, events, entry.second, ancestors);
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
    EmitExpanded(output, events, document, ancestors);
    if (!output.good()) throw std::runtime_error(output.GetLastError());
    std::cout << output.c_str() << '\n';
    return std::cout ? EXIT_SUCCESS : EXIT_FAILURE;
} catch (const std::exception& error) {
    std::cerr << error.what() << '\n';
    return EXIT_FAILURE;
}
