// Verified projection identities shared by flow, loans and native addressing.
class IrPlaceGraph {
    const std::vector<IrPlace>& places;
    std::map<int,std::size_t> indices;
    std::map<std::pair<int,int>,int> fields;std::map<int,std::size_t> depths;
    [[noreturn]] static void invalid(Location at={}){fail("AI3100","Native typed place projection invariant failed.",at);}
public:
    explicit IrPlaceGraph(const std::vector<IrPlace>& source):places(source){
        if(places.size()>65536)invalid();
        for(std::size_t i=0;i<places.size();++i)if(!indices.emplace(places[i].id,i).second)invalid(places[i].at);
        for(auto& p:places){
            if(p.parent==-1){if(p.field!=-1)invalid(p.at);depths.emplace(p.id,0);continue;}
            auto parent=indices.find(p.parent);if(parent==indices.end() || p.parent==p.id)invalid(p.at);auto& container=places[parent->second];
            if(!container.type.is_aggregate() || p.field<0 || static_cast<std::size_t>(p.field)>=container.type.definition().fields.size() || p.type!=container.type.field_type(static_cast<std::size_t>(p.field)) || p.parameter || p.mutable_!=container.mutable_ || p.constant!=container.constant || !fields.emplace(std::pair{p.parent,p.field},p.id).second)invalid(p.at);
            int id=p.parent;std::size_t depth=0;while(id!=-1){if(++depth>128)invalid(p.at);auto ancestor=indices.find(id);if(ancestor==indices.end())invalid(p.at);id=places[ancestor->second].parent;}depths.emplace(p.id,depth);
        }
        for(auto& p:places)if(p.type.is_aggregate())for(std::size_t i=0;i<p.type.definition().fields.size();++i)if(!fields.contains({p.id,static_cast<int>(i)}))invalid(p.at);
    }
    std::size_t depth(int id)const{auto it=depths.find(id);return it==depths.end() ? 0 : it->second;}
    bool contains(int id)const{return indices.contains(id);}
    std::size_t index(int id)const{auto it=indices.find(id);if(it==indices.end())invalid();return it->second;}
    const IrPlace& at(int id)const{return places[index(id)];}
    int child(int id,int field)const{auto it=fields.find({id,field});if(it==fields.end())invalid(at(id).at);return it->second;}
    std::vector<int> children(int id)const{std::vector<int> result;auto& p=at(id);if(p.type.is_aggregate())for(std::size_t i=0;i<p.type.definition().fields.size();++i)result.push_back(child(id,static_cast<int>(i)));return result;}
    std::vector<int> subtree(int id)const{std::vector<int> result,pending={id};while(!pending.empty()){int next=pending.back();pending.pop_back();result.push_back(next);auto nested=children(next);pending.insert(pending.end(),nested.rbegin(),nested.rend());}return result;}
    std::vector<int> parents(int id)const{std::vector<int> result;int parent=at(id).parent;while(parent!=-1){result.push_back(parent);parent=at(parent).parent;}return result;}
    std::vector<int> path(int id)const{std::vector<int> result;while(at(id).parent!=-1){result.push_back(at(id).field);id=at(id).parent;}std::reverse(result.begin(),result.end());return result;}
    int root(int id)const{while(at(id).parent!=-1)id=at(id).parent;return id;}
    bool overlaps(int a,int b)const{
        if(a==b)return true;if(!contains(a) || !contains(b) || root(a)!=root(b))return false;
        auto left=path(a),right=path(b);int parent=root(a);auto count=std::min(left.size(),right.size());
        for(std::size_t i=0;i<count;++i){if(left[i]!=right[i]){auto type=at(parent).type;return type.is_payload_union() && type.field_case(static_cast<std::size_t>(left[i]))!=type.field_case(static_cast<std::size_t>(right[i]));}parent=child(parent,left[i]);}return true;
    }
    std::string cplace(int id)const{int parent=root(id);auto result="v"+std::to_string(parent);for(int field:path(id)){auto type=at(parent).type;if(type.is_payload_union())result+=".data.c"+std::to_string(type.field_case(static_cast<std::size_t>(field)));result+=".m"+std::to_string(field);parent=child(parent,field);}return result;}
};
