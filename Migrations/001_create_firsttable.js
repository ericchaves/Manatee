{
    up:{
        create_table:{
            name:'FirstTable',
            columns:[
                {name:'name', type:'string'},
                {name:'description', type:'text'},
                {name:'price',type:'money'},
				{ name: 'usage_count', type: 'int', def: '0', nullable: false}
             ],
            timestamps:true
         }
    }
}